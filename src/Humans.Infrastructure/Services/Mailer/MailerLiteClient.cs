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
/// Caching MailerLite client. Registered as a Singleton so the in-memory
/// subscriber list, account summary, and group list survive across requests.
/// First read after startup (or after <see cref="RefreshAsync"/>) populates
/// the cache from MailerLite; every subsequent read is served from RAM.
/// Admins force a re-pull via POST /Mailer/Admin/Refresh.
///
/// Writes (CreateGroup, Assign/Unassign, BulkImport) are runtime-guarded:
/// every write either inspects the requested group name (CreateGroup) or
/// looks up the target group by id (Assign/Unassign/BulkImport) and throws
/// <see cref="InvalidOperationException"/> if the name doesn't start with
/// "Humans - ". Pinned by <c>MailerLiteClientWriteGuardTests</c>.
/// </summary>
public sealed class MailerLiteClient : IMailerLiteService
{
    public const string HttpClientName = "mailerlite";
    private const string HumansGroupPrefix = "Humans - ";

    private static readonly JsonSerializerOptions Json = BuildJson();
    private readonly IHttpClientFactory _httpFactory;
    private readonly IClock _clock;
    private readonly ILogger<MailerLiteClient> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IReadOnlyList<MailerLiteSubscriber>? _subscribers;
    private MailerLiteAccountSummary? _summary;
    private IReadOnlyList<MailerLiteGroup>? _groups;
    private Instant? _lastFetchedAt;

    public MailerLiteClient(
        IHttpClientFactory httpFactory,
        IClock clock,
        ILogger<MailerLiteClient> logger)
    {
        _httpFactory = httpFactory;
        _clock = clock;
        _logger = logger;
    }

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
        // Single-email lookup is a passthrough — callers asking for a specific
        // address want live state, not a snapshot from the dashboard cache.
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

    // NOTE: Assign/Unassign/BulkImport intentionally do NOT call InvalidateSubscribersCache().
    // They are called in tight loops by MailerAudienceSyncService, which holds its own
    // per-sync subscriber snapshot — invalidating mid-loop would force a full ML re-fetch
    // before every single write (N+M extra full-list calls for N assigns + M unassigns,
    // burning the rate limit). The cache will refresh on the next admin "Refresh" click
    // or via the singleton expiry path; the staleness window is bounded and harmless.

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

        // MailerLite's documented bulk-import endpoint
        // (POST /api/groups/{id}/import-subscribers) is asynchronous: it returns
        // an import_progress_url and we'd have to poll. Per-email upsert via
        // POST /api/subscribers with the groups array is synchronous, returns
        // the subscriber object directly, and is fine at our ~500-user scale.
        // ML treats it as upsert-or-update keyed on email.
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
                _logger.LogWarning(
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

    // Merges a newly-created group into the cached list under the gate so the
    // next RequireHumansGroupAsync call doesn't trigger a full re-populate.
    // Nullifying _groups would cascade into EnsurePopulatedAsync re-fetching the
    // entire subscriber list too — eviction would be disproportionate to the
    // change. If the cache hasn't been populated yet, this is a no-op; the first
    // read will pull the new group along with everything else.
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

    // Pulls everything fresh. If any underlying call throws, the prior cache
    // (if any) is left intact — callers see the exception and can retry.
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
        _lastFetchedAt = _clock.GetCurrentInstant();
    }

    private async IAsyncEnumerable<MailerLiteSubscriber> FetchSubscribersAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        string? cursor = null;
        while (true)
        {
            // include=groups is required — ML's list endpoint omits the per-subscriber
            // groups array without it, which leaves GroupIds empty and breaks both the
            // audience "currently in group" stat and the diff that suppresses re-assigns.
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

    // Internal seam for tests — calls SendAsync without the GET-only guard
    // (the guard was removed when outbound writes landed).
    internal Task<HttpResponseMessage> SendForTestsAsync(HttpMethod method, string url, CancellationToken ct)
        => SendAsync(method, url, content: null, ct);

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string url, HttpContent? content, CancellationToken ct)
    {
        var http = _httpFactory.CreateClient(HttpClientName);
        using var req = new HttpRequestMessage(method, url);
        if (content is not null) req.Content = content;
        HttpResponseMessage resp;
        try
        {
            resp = await http.SendAsync(req, ct);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "MailerLite HTTP call failed: {Method} {Url}", method, url);
            throw;
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // Timeout (HttpClient timeout fires TaskCanceledException with no token cancel).
            _logger.LogError(ex, "MailerLite HTTP call timed out: {Method} {Url}", method, url);
            throw;
        }
        if (resp.Headers.TryGetValues("X-RateLimit-Remaining", out var values)
            && int.TryParse(values.FirstOrDefault(), CultureInfo.InvariantCulture, out var remaining))
        {
            // Reactive back-off — ML allows 120 req/min and returns Remaining in every
            // response. Sleep proportionally as the window drains so tight write loops
            // (Assign/Unassign/BulkImport) don't slam into 429s. Tiers are intentionally
            // wide; 429-with-Retry-After handling is a separate concern.
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
                    // Caller never sees this response — dispose it so we don't leak the socket.
                    resp.Dispose();
                    throw;
                }
            }
        }
        if (!resp.IsSuccessStatusCode)
            _logger.LogWarning("MailerLite returned {StatusCode}: {Method} {Url}",
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

    // NOTE: MailerLiteSubscriber.FirstName and LastName will be null at runtime because
    // MailerLite stores name/last_name under a nested "fields" object, not at the
    // top level of the subscriber JSON. The spec says the importer preview doesn't
    // surface ML's name in this slice, so null is acceptable here.

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
