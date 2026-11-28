using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Humans.Base.Extensions;
using Humans.Base.Http;
using Humans.Base.Helpers;
using Humans.Holded.Contracts;
using Microsoft.Extensions.Options;
using NodaTime;
using static Humans.Holded.Services.HoldedJson;

namespace Humans.Holded.Services;

internal sealed class HoldedTransport(
    HttpClient http,
    IOptions<HoldedClientOptions> options,
    ILogger<HoldedClient> logger,
    IHoldedCallLog callLog,
    IClock clock) : ApiClientBase(http, options.Value.BaseUrl)
{
    private const int DefaultRetryAfterSeconds = 5;
    private const int MaxRetryAfterSeconds = 60;

    private readonly HoldedClientOptions _options = options.Value;

    protected override string ApiName => "Holded";

    protected override Exception Transient(string message, Exception? inner = null) =>
        inner is null ? new HoldedTransientException(message) : new HoldedTransientException(message, inner);

    protected override Exception Permanent(string message, HttpStatusCode? status = null, string? body = null) =>
        status is { } code ? new HoldedPermanentException((int)code, body, message) : new HoldedPermanentException(message);

    protected override void Authorize(HttpRequestMessage request) =>
        request.Headers.Authorization = Authorization
            ?? throw new HoldedPermanentException("HOLDED_API_KEY_V2 must be nonblank and valid in an HTTP authorization header.");

    protected override void OnResponse(HttpRequestMessage request, HttpResponseMessage response, string caller) =>
        RecordCall(caller, request.Method.Method, response);

    protected override Exception Failure(HttpResponseMessage response, string body, string caller)
    {
        var status = (int)response.StatusCode;
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            return new HoldedTransientException(
                $"Holded 429 Too Many Requests (retry after {ReadRetryAfterSeconds(response)?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}s)");
        if (status >= 500)
            return new HoldedTransientException($"Holded {status} {response.ReasonPhrase}");
        if (response.StatusCode == HttpStatusCode.Forbidden && MissingKeyPermission(body) is { } permission)
            return new HoldedPermanentException(403, body,
                $"Humans' Holded key lacks {permission}; add it in Holded > Settings > API");
        // This message reaches logs, LastError, the audit trail and the finance UI, and a 4xx on a contact
        // create can echo the IBAN back: mask it here. ResponseBody keeps the raw body.
        return new HoldedPermanentException(status, body,
            $"Holded {status} {response.ReasonPhrase}: {MaskDiagnosticBody(body)}");
    }

    public bool IsConfigured => HasBaseAddress && Authorization is not null;

    private AuthenticationHeaderValue? Authorization =>
        !string.IsNullOrWhiteSpace(_options.ApiKey)
        && AuthenticationHeaderValue.TryParse($"Bearer {_options.ApiKey}", out var header) ? header : null;

    /// <summary>Throws at pageSafetyCap rather than returning a truncated list: these feed replace-semantics
    /// reconciliation, so a short fetch would delete rows that still exist in Holded.</summary>
    public async Task<List<JsonNode>> GetPagedAsync(
        string pathAndQuery, int pageSafetyCap, CancellationToken ct,
        [CallerMemberName] string caller = "")
    {
        var endpoint = pathAndQuery.Split('?', 2)[0];
        var items = new List<JsonNode>();
        await foreach (var page in GetPagesAsync(endpoint, cursor => ReadPageAsync(pathAndQuery, endpoint, cursor, caller, ct), pageSafetyCap, ct))
            items.AddRange(page);
        return items;
    }

    private async Task<(List<JsonNode> Page, string? Next)> ReadPageAsync(
        string pathAndQuery, string endpoint, string? cursor, string caller, CancellationToken ct)
    {
        var url = cursor is null ? pathAndQuery : $"{pathAndQuery}&cursor={Uri.EscapeDataString(cursor)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        // Pass the real caller, or the call log records every paged endpoint as "ReadPageAsync".
        using var resp = await SendWithRetryAsync(req, ct, caller);
        try
        {
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            var root = await JsonNode.ParseAsync(stream, cancellationToken: ct);

            // Strict, not Prop/Arr: a 200 carrying Holded's {"status":0,...} error, or any body without items,
            // would read as an empty page, and a false empty deletes every cached row in the window.
            if (Prop(root, "items") is not JsonArray itemsArr)
            {
                var preview = MaskDiagnosticBody(root?.ToJsonString() ?? "null");
                throw new HoldedTransientException(
                    $"Holded returned a 200 without a valid items array for {endpoint} " +
                    $"(body starts: {preview[..Math.Min(preview.Length, 120)]}).");
            }
            var items = new List<JsonNode>();
            foreach (var n in itemsArr)
                items.Add(n ?? throw new HoldedPermanentException($"Holded page for {endpoint} contains a null item."));

            // Absent has_more is a legitimate final page (accounting-accounts sends no pagination metadata). But
            // has_more:true without a cursor can't be followed, and the prefix would feed replace semantics.
            var hasMore = Prop(root, "has_more")?.GetValue<bool>() ?? false;
            var next = Prop(root, "cursor")?.GetValue<string>();
            if (!hasMore) return (items, null);
            if (string.IsNullOrEmpty(next))
                throw new HoldedTransientException(
                    $"Holded page for {endpoint} claims has_more but carries no cursor.");
            return (items, next);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException
            or FormatException or OverflowException)
        {
            // A malformed successful body would escape as a raw JsonException, which no caller handles: /Finance/Sepa
            // would fail instead of warning and the SEPA sweep would abort. Normalized here, where every paged
            // endpoint shares it.
            throw new HoldedPermanentException(
                $"Holded page for {endpoint} could not be read.", ex);
        }
    }

    /// <summary>Sends, retrying a content-free GET once after a 429, and returns only a success.</summary>
    public async Task<HttpResponseMessage> SendWithRetryAsync(
        HttpRequestMessage req, CancellationToken ct,
        [CallerMemberName] string caller = "")
    {
        using var _ = logger.TimeOperation(operation: caller);
        var resp = await SendRawAsync(req, ct, caller);

        // Only content-free GETs are safe to retry here. A bodyless approval POST still
        // mutates Holded, so the absence of content cannot establish repeatability.
        if (resp.StatusCode == HttpStatusCode.TooManyRequests && req.Method == HttpMethod.Get && req.Content is null)
        {
            var retryAfterSeconds = Math.Min(
                ReadRetryAfterSeconds(resp) ?? DefaultRetryAfterSeconds, MaxRetryAfterSeconds);
            resp.Dispose();
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Ceiling(retryAfterSeconds * 1000)), ct);
            using var retry = CloneForRetry(req);
            resp = await SendRawAsync(retry, ct, caller);
        }

        return await EnsureSuccessAsync(resp, ct, caller);
    }

    /// <summary>The scope Holded names in <c>Insufficient permissions: X required</c>, or null.</summary>
    internal static string? MissingKeyPermission(string body)
    {
        var m = MissingPermissionPattern.Match(body);
        return m.Success ? m.Groups["scope"].Value.Trim() : null;
    }

    private static readonly System.Text.RegularExpressions.Regex MissingPermissionPattern = new(
        @"Insufficient permissions:\s*(?<scope>[^""]+?)\s+required",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(1));

    private static string MaskDiagnosticBody(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            using var output = new MemoryStream();
            // Relaxed escaping keeps Spanish vendor text readable; the message is HTML-encoded wherever it renders.
            using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions
            { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
                WriteMasked(document.RootElement, writer);
            return System.Text.Encoding.UTF8.GetString(output.ToArray());
        }
        catch (JsonException)
        {
            return IbanFormatter.MaskAllIn(body);
        }

        static void WriteMasked(JsonElement element, Utf8JsonWriter writer)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();
                    foreach (var property in element.EnumerateObject())
                    {
                        writer.WritePropertyName(IbanFormatter.MaskAllIn(property.Name));
                        WriteMasked(property.Value, writer);
                    }
                    writer.WriteEndObject();
                    break;
                case JsonValueKind.Array:
                    writer.WriteStartArray();
                    foreach (var item in element.EnumerateArray()) WriteMasked(item, writer);
                    writer.WriteEndArray();
                    break;
                case JsonValueKind.String:
                    writer.WriteStringValue(IbanFormatter.MaskAllIn(element.GetString()));
                    break;
                default:
                    element.WriteTo(writer);
                    break;
            }
        }
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

        callLog.Record(new HoldedApiCallRecord(
            clock.GetCurrentInstant(), endpoint, method, (int)resp.StatusCode,
            rateLimitRemaining, rateLimitWindow));
    }

    private double? ReadRetryAfterSeconds(HttpResponseMessage resp)
    {
        var retryAfter = resp.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta) return delta.TotalSeconds;
        // Retry-After also has a valid HTTP-date form; a past date clamps to 0 (retry now).
        if (retryAfter?.Date is { } date)
            return Math.Max(0, (date - clock.GetCurrentInstant().ToDateTimeOffset()).TotalSeconds);
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
