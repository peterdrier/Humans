using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Humans.Base.Extensions;
using Humans.Base.Helpers;
using Humans.Holded.Contracts;
using Microsoft.Extensions.Options;
using NodaTime;
using static Humans.Holded.Services.HoldedJson;

namespace Humans.Holded.Services;

internal sealed class HoldedTransport
{
    private const int DefaultRetryAfterSeconds = 5;
    private const int MaxRetryAfterSeconds = 60;

    private readonly HttpClient _http;
    private readonly HoldedClientOptions _options;
    private readonly ILogger<HoldedClient> _logger;
    private readonly IHoldedCallLog _callLog;
    private readonly IClock _clock;

    public HoldedTransport(
        HttpClient http,
        IOptions<HoldedClientOptions> options,
        ILogger<HoldedClient> logger,
        IHoldedCallLog callLog,
        IClock clock)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
        _callLog = callLog;
        _clock = clock;

        if (_http.BaseAddress is null && !string.IsNullOrEmpty(_options.BaseUrl))
            _http.BaseAddress = new Uri(_options.BaseUrl);
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.ApiKey);

    /// <summary>Throws at pageSafetyCap rather than returning a truncated list: these feed replace-semantics
    /// reconciliation, so a short fetch would delete rows that still exist in Holded.</summary>
    public async Task<List<JsonNode>> GetPagedAsync(
        string pathAndQuery, int pageSafetyCap, CancellationToken ct,
        [CallerMemberName] string caller = "")
    {
        var items = new List<JsonNode>();
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        try
        {
            for (var page = 1; page <= pageSafetyCap; page++)
            {
                var url = cursor is null
                    ? pathAndQuery
                    : $"{pathAndQuery}&cursor={Uri.EscapeDataString(cursor)}";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                // Pass the real caller, or the call log records every paged endpoint as "GetPagedAsync".
                using var resp = await SendAsync(req, ct, caller);
                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                var root = await JsonNode.ParseAsync(stream, cancellationToken: ct);

                // Strict, not Prop/Arr: a 200 carrying Holded's {"status":0,...} error, or any body without items,
                // would read as an empty page, and a false empty deletes every cached row in the window.
                if (Prop(root, "items") is not JsonArray itemsArr)
                {
                    var preview = root?.ToJsonString() ?? "null";
                    throw new HoldedTransientException(
                        $"Holded returned a 200 without a valid items array for {pathAndQuery.Split('?', 2)[0]} " +
                        $"(body starts: {preview[..Math.Min(preview.Length, 120)]}).");
                }
                foreach (var n in itemsArr)
                {
                    if (n is null)
                        throw new HoldedPermanentException(
                            $"Holded page for {pathAndQuery.Split('?', 2)[0]} contains a null item.");
                    items.Add(n);
                }

                // Absent has_more is a legitimate final page (accounting-accounts sends no pagination metadata). But
                // has_more:true without a cursor can't be followed, and the prefix would feed replace semantics.
                var hasMore = Prop(root, "has_more")?.GetValue<bool>() ?? false;
                cursor = Prop(root, "cursor")?.GetValue<string>();
                if (!hasMore) return items;
                if (string.IsNullOrEmpty(cursor))
                    throw new HoldedTransientException(
                        $"Holded page for {pathAndQuery.Split('?', 2)[0]} claims has_more but carries no cursor.");
                if (!seenCursors.Add(cursor))
                    throw new HoldedTransientException(
                        $"Holded page for {pathAndQuery.Split('?', 2)[0]} carries a repeated cursor.");
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException
            or FormatException or OverflowException)
        {
            // A malformed successful body would escape as a raw JsonException, which no caller handles: /Finance/Sepa
            // would fail instead of warning and the SEPA sweep would abort. Normalized here, where every paged
            // endpoint shares it.
            throw new HoldedPermanentException(
                $"Holded page for {pathAndQuery.Split('?', 2)[0]} could not be read.", ex);
        }

        var endpoint = pathAndQuery.Split('?', 2)[0];
        throw new HoldedTransientException(
            $"Holded cursor pagination for {endpoint} hit the {pageSafetyCap}-page safety cap.");
    }

    private void AttachAuth(HttpRequestMessage req) =>
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

    public async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage req, CancellationToken ct,
        [CallerMemberName] string caller = "")
    {
        AttachAuth(req);
        using var _ = _logger.TimeOperation(operation: caller);
        var resp = await SendOnceAsync(req, caller, ct);

        // Only content-free GETs are safe to retry here. A bodyless approval POST still
        // mutates Holded, so the absence of content cannot establish repeatability.
        if (resp.StatusCode == HttpStatusCode.TooManyRequests && req.Method == HttpMethod.Get && req.Content is null)
        {
            var retryAfterSeconds = Math.Min(
                ReadRetryAfterSeconds(resp) ?? DefaultRetryAfterSeconds, MaxRetryAfterSeconds);
            resp.Dispose();
            await Task.Delay(TimeSpan.FromSeconds(retryAfterSeconds), ct);
            using var retry = CloneForRetry(req);
            resp = await SendOnceAsync(retry, caller, ct);
        }

        if (resp.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfterSeconds = ReadRetryAfterSeconds(resp);
            using (resp)
            {
                throw new HoldedTransientException(
                    $"Holded 429 Too Many Requests (retry after {retryAfterSeconds?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}s)");
            }
        }

        if (resp.IsSuccessStatusCode) return resp;

        using (resp)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            if ((int)resp.StatusCode >= 500)
                throw new HoldedTransientException(
                    $"Holded {(int)resp.StatusCode} {resp.ReasonPhrase}");
            // This message reaches logs, LastError, the audit trail and the finance UI, and a 4xx on a contact
            // create can echo the IBAN back: mask it here. ResponseBody keeps the raw body.
            throw new HoldedPermanentException((int)resp.StatusCode, body,
                $"Holded {(int)resp.StatusCode} {resp.ReasonPhrase}: {IbanFormatter.MaskAllIn(body)}");
        }
    }

    private async Task<HttpResponseMessage> SendOnceAsync(
        HttpRequestMessage req, string caller, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new HoldedTransientException("Holded HTTP send failed", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new HoldedTransientException("Holded HTTP send timed out", ex);
        }

        RecordCall(caller, req.Method.Method, resp);
        return resp;
    }

    private void RecordCall(string endpoint, string method, HttpResponseMessage resp)
    {
        int? rateLimitRemaining = resp.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining)
            && int.TryParse(remaining.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
        var rateLimitWindow = resp.Headers.TryGetValues("X-RateLimit-Window", out var window)
            ? window.FirstOrDefault()
            : null;

        _callLog.Record(new HoldedApiCallRecord(
            _clock.GetCurrentInstant(), endpoint, method, (int)resp.StatusCode,
            rateLimitRemaining, rateLimitWindow));
    }

    private int? ReadRetryAfterSeconds(HttpResponseMessage resp)
    {
        var retryAfter = resp.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta) return (int)delta.TotalSeconds;
        // Retry-After also has a valid HTTP-date form; a past date clamps to 0 (retry now).
        if (retryAfter?.Date is { } date)
            return (int)Math.Max(0, (date - _clock.GetCurrentInstant().ToDateTimeOffset()).TotalSeconds);
        return null;
    }

    /// <summary>A request can be sent only once; the 429 retry needs a fresh copy with the same headers (auth too).</summary>
    private static HttpRequestMessage CloneForRetry(HttpRequestMessage req)
    {
        var clone = new HttpRequestMessage(req.Method, req.RequestUri);
        foreach (var header in req.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return clone;
    }
}
