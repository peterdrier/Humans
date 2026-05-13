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
/// </summary>
public sealed class MailerLiteClient : IMailerLiteService
{
    public const string HttpClientName = "mailerlite";

    private static readonly JsonSerializerOptions Json = BuildJson();
    private readonly IHttpClientFactory _httpFactory;
    private readonly IClock _clock;
    private readonly ILogger<MailerLiteClient> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IReadOnlyList<MailerLiteSubscriber>? _subscribers;
    private MailerLiteAccountSummary? _summary;
    private IReadOnlyList<MailerLiteGroup>? _groups;
    private Instant? _lastFetchedAt;

    public MailerLiteClient(IHttpClientFactory httpFactory, IClock clock, ILogger<MailerLiteClient> logger)
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
            $"/api/subscribers/{Uri.EscapeDataString(email)}", ct);
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
            var url = "/api/subscribers?limit=100";
            if (cursor is not null) url += $"&cursor={Uri.EscapeDataString(cursor)}";
            using var resp = await SendAsync(HttpMethod.Get, url, ct);
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
            using var resp = await SendAsync(HttpMethod.Get, $"/api/groups?page={page}&limit=100", ct);
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadFromJsonAsync<GroupListEnvelope>(Json, ct);
            if (body is null || body.Data.Count == 0) break;
            results.AddRange(body.Data);
            if (body.Meta.CurrentPage >= body.Meta.LastPage) break;
            page++;
        }
        return results;
    }

    // Internal seam for tests — exposes the private guard.
    internal Task<HttpResponseMessage> SendForTestsAsync(HttpMethod method, string url, CancellationToken ct)
        => SendAsync(method, url, ct);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, CancellationToken ct)
    {
        if (method != HttpMethod.Get)
            throw new InvalidOperationException(
                $"MailerLite client is read-only. Attempted {method} {url}. " +
                "Outbound writes belong to a separate slice with its own review.");
        var http = _httpFactory.CreateClient(HttpClientName);
        using var req = new HttpRequestMessage(method, url);
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
            && int.TryParse(values.FirstOrDefault(), CultureInfo.InvariantCulture, out var remaining) && remaining < 20)
            _logger.LogWarning("MailerLite rate limit remaining: {Remaining}", remaining);
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
}
