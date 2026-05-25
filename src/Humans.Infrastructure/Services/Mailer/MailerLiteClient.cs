using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Humans.Application.Interfaces.Mailer;
using Humans.Application.Interfaces.Mailer.Dtos;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Infrastructure.Services.Mailer;

/// <summary>
/// Caching MailerLite client (Singleton). Writes are restricted to groups whose
/// name starts with "Humans - "; pinned by MailerLiteClientWriteGuardTests.
/// </summary>
public sealed class MailerLiteClient(IHttpClientFactory httpFactory, IClock clock, ILogger<MailerLiteClient> logger)
    : IMailerLiteService
{
    public const string HttpClientName = "mailerlite";
    private const string HumansGroupPrefix = "Humans - ";

    private static readonly JsonSerializerOptions Json = BuildJson();
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IReadOnlyList<MailerLiteSubscriber>? _subscribers;
    private MailerLiteAccountSummary? _summary;
    private IReadOnlyList<MailerLiteGroup>? _groups;
    private Instant? _lastFetchedAt;

    public Instant? LastFetchedAt => _lastFetchedAt;

    public async Task<MailerLiteAccountSummary> GetAccountSummaryAsync(CancellationToken ct = default)
    {
        await EnsurePopulatedAsync(ct);
        return _summary!;
    }

    public async Task<IReadOnlyList<MailerLiteGroup>> ListGroupsAsync(CancellationToken ct = default)
    {
        await EnsurePopulatedAsync(ct);
        return _groups!;
    }

    public async IAsyncEnumerable<MailerLiteSubscriber> ListSubscribersAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await EnsurePopulatedAsync(ct);
        foreach (var s in _subscribers!) yield return s;
    }

    public async Task<MailerLiteSubscriber?> GetSubscriberAsync(string email, CancellationToken ct = default)
    {
        // Passthrough — callers want live state, not the dashboard snapshot.
        using var resp = await SendAsync(HttpMethod.Get,
            $"/api/subscribers/{Uri.EscapeDataString(email)}", content: null, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<SubscriberSingleEnvelope>(Json, ct);
        return body?.Data;
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await PopulateLockedAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<MailerLiteGroup> CreateGroupAsync(string name, CancellationToken ct = default)
    {
        if (!name.StartsWith(HumansGroupPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Group name '{name}' must start with '{HumansGroupPrefix}'.");

        using var body = JsonContent.Create(new { name }, options: Json);
        using var resp = await SendAsync(HttpMethod.Post, "/api/groups", body, ct);
        resp.EnsureSuccessStatusCode();
        var env = await resp.Content.ReadFromJsonAsync<GroupSingleEnvelope>(Json, ct)
            ?? throw new InvalidOperationException("MailerLite returned empty body on CreateGroup.");

        await AppendToGroupsCacheAsync(env.Data, ct);
        return env.Data;
    }

    // Assign/Unassign/BulkImport deliberately don't invalidate — the sync service holds its
    // own snapshot and tight-loop invalidation would burn the rate limit.

    public async Task AssignSubscriberToGroupAsync(
        string subscriberId, string groupId, CancellationToken ct = default)
    {
        await RequireHumansGroupAsync(groupId, ct);

        using var resp = await SendAsync(
            HttpMethod.Post,
            $"/api/subscribers/{Uri.EscapeDataString(subscriberId)}/groups/{Uri.EscapeDataString(groupId)}",
            content: null, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task UnassignSubscriberFromGroupAsync(
        string subscriberId, string groupId, CancellationToken ct = default)
    {
        await RequireHumansGroupAsync(groupId, ct);

        using var resp = await SendAsync(
            HttpMethod.Delete,
            $"/api/subscribers/{Uri.EscapeDataString(subscriberId)}/groups/{Uri.EscapeDataString(groupId)}",
            content: null, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<BulkImportResult> BulkImportSubscribersToGroupAsync(
        string groupId, IReadOnlyList<string> emails, CancellationToken ct = default)
    {
        await RequireHumansGroupAsync(groupId, ct);
        if (emails.Count == 0)
            return new BulkImportResult(0, 0, 0, 0);

        // Per-email upsert is synchronous; ML's bulk import endpoint requires polling.
        int created = 0, errors = 0;

        foreach (var email in emails)
        {
            var payload = new
            {
                email,
                groups = new[] { groupId },
            };
            using var body = JsonContent.Create(payload, options: Json);
            using var resp = await SendAsync(HttpMethod.Post, "/api/subscribers", body, ct);
            if (!resp.IsSuccessStatusCode)
            {
                errors++;
                logger.LogWarning(
                    "Subscriber upsert failed for {Email} into group {GroupId}: {StatusCode}",
                    email, groupId, (int)resp.StatusCode);
                continue;
            }
            created++;
        }

        return new BulkImportResult(Created: created, Updated: 0, Duplicates: 0, Errors: errors);
    }

    private async Task<MailerLiteGroup> RequireHumansGroupAsync(string groupId, CancellationToken ct)
    {
        var groups = await ListGroupsAsync(ct);
        var group = groups.FirstOrDefault(g => string.Equals(g.Id, groupId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"MailerLite group '{groupId}' not found.");
        if (!group.Name.StartsWith(HumansGroupPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"MailerLite group '{group.Name}' (id={groupId}) is not managed by Humans. " +
                $"Writes are restricted to groups whose name starts with '{HumansGroupPrefix}'.");
        return group;
    }

    // Merge under the gate — nullifying would cascade into a full subscriber re-fetch.
    private async Task AppendToGroupsCacheAsync(MailerLiteGroup newGroup, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_groups is not null)
                _groups = _groups.Append(newGroup).ToList();
        }
        finally { _gate.Release(); }
    }

    private async Task EnsurePopulatedAsync(CancellationToken ct)
    {
        if (_subscribers is not null && _summary is not null && _groups is not null)
            return;
        await _gate.WaitAsync(ct);
        try
        {
            if (_subscribers is not null && _summary is not null && _groups is not null)
                return;
            await PopulateLockedAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Throw leaves the prior cache intact; callers retry.
    private async Task PopulateLockedAsync(CancellationToken ct)
    {
        var subscribers = new List<MailerLiteSubscriber>();
        int active = 0, unsub = 0, unc = 0, bnc = 0, jnk = 0;
        await foreach (var s in FetchSubscribersAsync(ct))
        {
            subscribers.Add(s);
            switch (s.Status)
            {
                case "active": active++; break;
                case "unsubscribed": unsub++; break;
                case "unconfirmed": unc++; break;
                case "bounced": bnc++; break;
                case "junk": jnk++; break;
            }
        }
        var groups = await FetchGroupsAsync(ct);

        _subscribers = subscribers;
        _summary = new MailerLiteAccountSummary(active, unsub, unc, bnc, jnk);
        _groups = groups;
        _lastFetchedAt = clock.GetCurrentInstant();
    }

    private async IAsyncEnumerable<MailerLiteSubscriber> FetchSubscribersAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        string? cursor = null;
        while (true)
        {
            // include=groups required — omitting it returns empty GroupIds.
            var url = "/api/subscribers?limit=100&include=groups";
            if (cursor is not null) url += $"&cursor={Uri.EscapeDataString(cursor)}";
            using var resp = await SendAsync(HttpMethod.Get, url, content: null, ct);
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadFromJsonAsync<SubscriberListEnvelope>(Json, ct);
            if (body is null) yield break;
            foreach (var s in body.Data) yield return s;
            if (string.IsNullOrEmpty(body.Meta.NextCursor)) yield break;
            cursor = body.Meta.NextCursor;
        }
    }

    private async Task<IReadOnlyList<MailerLiteGroup>> FetchGroupsAsync(CancellationToken ct)
    {
        var results = new List<MailerLiteGroup>();
        int page = 1;
        while (true)
        {
            using var resp = await SendAsync(HttpMethod.Get, $"/api/groups?page={page}&limit=100", content: null, ct);
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadFromJsonAsync<GroupListEnvelope>(Json, ct);
            if (body is null || body.Data.Count == 0) break;
            results.AddRange(body.Data);
            if (body.Meta.CurrentPage >= body.Meta.LastPage) break;
            page++;
        }
        return results;
    }

    // Test seam.
    internal Task<HttpResponseMessage> SendForTestsAsync(HttpMethod method, string url, CancellationToken ct)
        => SendAsync(method, url, content: null, ct);

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string url, HttpContent? content, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(HttpClientName);
        using var req = new HttpRequestMessage(method, url);
        if (content is not null) req.Content = content;
        HttpResponseMessage resp;
        try
        {
            resp = await http.SendAsync(req, ct);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "MailerLite HTTP call failed: {Method} {Url}", method, url);
            throw;
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // HttpClient timeout — surfaces as TaskCanceledException with no token cancel.
            logger.LogError(ex, "MailerLite HTTP call timed out: {Method} {Url}", method, url);
            throw;
        }
        if (resp.Headers.TryGetValues("X-RateLimit-Remaining", out var values)
            && int.TryParse(values.FirstOrDefault(), CultureInfo.InvariantCulture, out var remaining))
        {
            // Reactive back-off — ML allows 120 req/min; drain proportionally to avoid 429s.
            var delay = remaining switch
            {
                < 5 => TimeSpan.FromSeconds(5),
                < 10 => TimeSpan.FromSeconds(2),
                < 20 => TimeSpan.FromSeconds(1),
                _ => TimeSpan.Zero,
            };
            if (delay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(delay, ct);
                }
                catch
                {
                    // Dispose so we don't leak the socket — caller never sees this response.
                    resp.Dispose();
                    throw;
                }
            }
        }
        if (!resp.IsSuccessStatusCode)
            logger.LogWarning("MailerLite returned {StatusCode}: {Method} {Url}",
                (int)resp.StatusCode, method, url);
        return resp;
    }

    private static JsonSerializerOptions BuildJson()
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true,
        };
        o.Converters.Add(new MailerLiteDateConverter());
        o.Converters.Add(new MailerLiteRequiredDateConverter());
        o.Converters.Add(new MailerLiteSubscriberConverter());
        return o;
    }

    // FirstName/LastName are null at runtime — ML stores them under nested "fields", not top-level.

    private sealed record SubscriberListEnvelope(
        [property: JsonPropertyName("data")] IReadOnlyList<MailerLiteSubscriber> Data,
        [property: JsonPropertyName("meta")] SubscriberMeta Meta);

    private sealed record SubscriberMeta(
        [property: JsonPropertyName("next_cursor")] string? NextCursor);

    private sealed record SubscriberSingleEnvelope(
        [property: JsonPropertyName("data")] MailerLiteSubscriber Data);

    private sealed record GroupListEnvelope(
        [property: JsonPropertyName("data")] IReadOnlyList<MailerLiteGroup> Data,
        [property: JsonPropertyName("meta")] GroupMeta Meta);

    private sealed record GroupMeta(
        [property: JsonPropertyName("current_page")] int CurrentPage,
        [property: JsonPropertyName("last_page")] int LastPage);

    private sealed record GroupSingleEnvelope(
        [property: JsonPropertyName("data")] MailerLiteGroup Data);
}
