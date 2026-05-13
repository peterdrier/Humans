using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Humans.Application.Interfaces.Mailer;
using Humans.Application.Interfaces.Mailer.Dtos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Humans.Infrastructure.Services.Mailer;

public sealed class MailerLiteClient : IMailerLiteService
{
    private static readonly JsonSerializerOptions Json = BuildJson();
    private readonly HttpClient _http;
    private readonly MailerLiteOptions _opts;
    private readonly ILogger<MailerLiteClient> _logger;

    public MailerLiteClient(
        HttpClient http,
        IOptions<MailerLiteOptions> opts,
        IClock _,
        ILogger<MailerLiteClient> logger)
    {
        _http = http;
        _opts = opts.Value;
        _logger = logger;
        _http.BaseAddress = new Uri(_opts.BaseUrl);
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _opts.ApiKey);
        _http.DefaultRequestHeaders.Add("X-Version", _opts.ApiVersion);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<MailerLiteAccountSummary> GetAccountSummaryAsync(CancellationToken ct = default)
    {
        // MailerLite v2 has no endpoint for subscriber-status totals. /api/subscribers
        // uses cursor pagination and the response 'meta' carries only path / per_page /
        // next_cursor / prev_cursor — no total. We do a single full sweep of subscribers
        // and bucket by status client-side.
        int active = 0, unsub = 0, unc = 0, bnc = 0, jnk = 0;
        await foreach (var s in ListSubscribersAsync(ct))
        {
            switch (s.Status)
            {
                case "active": active++; break;
                case "unsubscribed": unsub++; break;
                case "unconfirmed": unc++; break;
                case "bounced": bnc++; break;
                case "junk": jnk++; break;
            }
        }
        return new MailerLiteAccountSummary(active, unsub, unc, bnc, jnk);
    }

    public async Task<IReadOnlyList<MailerLiteGroup>> ListGroupsAsync(CancellationToken ct = default)
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

    public async IAsyncEnumerable<MailerLiteSubscriber> ListSubscribersAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
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

    public async Task<MailerLiteSubscriber?> GetSubscriberAsync(string email, CancellationToken ct = default)
    {
        using var resp = await SendAsync(HttpMethod.Get,
            $"/api/subscribers/{Uri.EscapeDataString(email)}", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<SubscriberSingleEnvelope>(Json, ct);
        return body?.Data;
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
        using var req = new HttpRequestMessage(method, url);
        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, ct);
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
    // TODO (Task 20): if ML names are needed, add a custom JsonConverter<MailerLiteSubscriber>
    // that reads fields.name → FirstName and fields.last_name → LastName.

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
