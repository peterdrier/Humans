using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Humans.Pleo.Services;

internal sealed class PleoClientOptions
{
    /// <summary>From the <c>PLEO_API_KEY</c> env var only, never appsettings.</summary>
    public string ApiKey { get; set; } = "";

    public string CompanyId { get; set; } = "";
    public string BaseUrl { get; set; } = "https://external.pleo.io";
}

internal sealed class PleoClient : IPleoClient
{
    private const int PageLimit = 100;

    /// <summary>Throws rather than returning a prefix: the sweep deletes whatever a fetch leaves out.</summary>
    private const int PageSafetyCap = 100;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly PleoClientOptions _options;

    public PleoClient(HttpClient http, IOptions<PleoClientOptions> options)
    {
        _http = http;
        _options = options.Value;
        _http.BaseAddress ??= new Uri(_options.BaseUrl);
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_options.ApiKey) && !string.IsNullOrWhiteSpace(_options.CompanyId);

    private string Company => Uri.EscapeDataString(_options.CompanyId);

    public Task<IReadOnlyList<JsonElement>> SearchEntriesAsync(CancellationToken ct = default) =>
        GetAllPagesAsync("accounting-entries:search", after =>
            Post($"/v1/accounting-entries:search?company_id={Company}&limit={PageLimit}{After(after)}", "{}"), ct);

    public async Task<IReadOnlyList<PleoAccountWire>> SearchAccountsAsync(CancellationToken ct = default)
    {
        // The company goes in the body: this endpoint rejects it as a query parameter.
        var body = JsonSerializer.Serialize(new { companyId = _options.CompanyId });
        var items = await GetAllPagesAsync("accounts:search", after =>
            Post($"/v1-beta/accounts:search?limit={PageLimit}{After(after)}", body), ct);
        return items.Select(Read<PleoAccountWire>).ToList();
    }

    public async Task<IReadOnlyList<PleoEmployeeWire>> ListEmployeesAsync(CancellationToken ct = default)
    {
        // Historical entries still reference deleted cardholders; an active-only sweep would erase their names.
        var items = await GetAllPagesAsync("employees", after =>
            new HttpRequestMessage(HttpMethod.Get, $"/v2/employees?companyId={Company}&includeDeleted=true&limit={PageLimit}{After(after)}"), ct);
        return items.Select(Read<PleoEmployeeWire>).ToList();
    }

    public async Task<PleoBalanceWire> GetBalanceAsync(CancellationToken ct = default)
    {
        var root = await SendAsync("balance",
            () => new HttpRequestMessage(HttpMethod.Get, $"/v1/companies/{Company}/balance"), ct);
        try
        {
            var accounts = root.GetProperty("data").GetProperty("accounts").EnumerateArray().ToList();
            var primary = accounts.FirstOrDefault(a =>
                a.TryGetProperty("isPrimary", out var p) && p.ValueKind == JsonValueKind.True);
            if (primary.ValueKind == JsonValueKind.Undefined)
                throw new PleoPermanentException($"Pleo balance: no primary account among {accounts.Count}.");
            var balance = primary.GetProperty("balance");
            var current = balance.GetProperty("current");
            return new PleoBalanceWire(
                current.GetProperty("currency").GetString() ?? "",
                current.GetProperty("value").GetInt64(),
                balance.GetProperty("available").GetProperty("value").GetInt64(),
                balance.GetProperty("pendingDebits").GetProperty("value").GetInt64());
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new PleoPermanentException("Pleo balance: unexpected body shape.", ex);
        }
    }

    public async Task<IReadOnlyList<PleoReceiptWire>> ListReceiptsAsync(string entryId, CancellationToken ct = default)
    {
        var items = await GetAllPagesAsync("receipts", after => new HttpRequestMessage(HttpMethod.Get,
            $"/v1/accounting-entries/{Uri.EscapeDataString(entryId)}/receipts?company_id={Company}{After(after)}"), ct);
        return items.Select(Read<PleoReceiptWire>).ToList();
    }

    public async Task<PleoReceiptFile> GetReceiptFileAsync(
        string entryId, string receiptId, CancellationToken ct = default)
    {
        // The signed URLs expire, so each view lists afresh rather than reusing one.
        var receipt = (await ListReceiptsAsync(entryId, ct)).FirstOrDefault(r => string.Equals(r.Id, receiptId, StringComparison.Ordinal))
            ?? throw new PleoPermanentException($"Pleo receipt {receiptId} is not on entry {entryId}.", 404);
        if (!Uri.TryCreate(receipt.Url, UriKind.Absolute, out var url) || !string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
            || !url.Host.EndsWith(".pleo.io", StringComparison.OrdinalIgnoreCase))
            throw new PleoPermanentException($"Pleo receipt {receiptId} has no Pleo download URL.");

        // No Authorization header: the URL is signed, and the key must never leave external.pleo.io.
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await SendRawAsync("receipt file", req, ct);
        var content = await resp.Content.ReadAsByteArrayAsync(ct);
        var type = resp.Content.Headers.ContentType?.MediaType ?? receipt.MimeType ?? "";
        // Served from our own origin: anything but an image or a PDF goes out as an opaque download.
        if (!type.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && !string.Equals(type, "application/pdf", StringComparison.OrdinalIgnoreCase))
            type = "application/octet-stream";
        return new PleoReceiptFile(content, type);
    }

    private static string After(string? cursor) =>
        cursor is null ? "" : $"&after={Uri.EscapeDataString(cursor)}";

    private static HttpRequestMessage Post(string pathAndQuery, string json) =>
        new(HttpMethod.Post, pathAndQuery) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static T Read<T>(JsonElement item) where T : class
    {
        try
        {
            return item.Deserialize<T>(Json) ?? throw new PleoPermanentException($"Pleo returned a null {typeof(T).Name}.");
        }
        catch (JsonException ex)
        {
            throw new PleoPermanentException($"Pleo returned an unreadable {typeof(T).Name}.", ex);
        }
    }

    private async Task<IReadOnlyList<JsonElement>> GetAllPagesAsync(
        string endpoint, Func<string?, HttpRequestMessage> request, CancellationToken ct)
    {
        var items = new List<JsonElement>();
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        string? after = null;
        for (var page = 1; page <= PageSafetyCap; page++)
        {
            var cursor = after;
            var root = await SendAsync(endpoint, () => request(cursor), ct);

            // Strict: a body without data would read as an empty page, and an empty sweep deletes the mirror.
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                throw new PleoTransientException($"Pleo {endpoint}: a 200 without a data array.");
            items.AddRange(data.EnumerateArray());

            if (!root.TryGetProperty("pagination", out var pagination)
                || pagination.ValueKind != JsonValueKind.Object
                || !pagination.TryGetProperty("hasNextPage", out var next)
                || next.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new PleoTransientException($"Pleo {endpoint}: a page without a boolean hasNextPage.");
            if (next.ValueKind == JsonValueKind.False) return items;

            after = pagination.TryGetProperty("endCursor", out var end) && end.ValueKind == JsonValueKind.String
                ? end.GetString()
                : null;
            if (string.IsNullOrEmpty(after))
                throw new PleoTransientException($"Pleo {endpoint}: hasNextPage without an endCursor.");
            if (!seenCursors.Add(after))
                throw new PleoTransientException($"Pleo {endpoint}: a repeated cursor.");
        }
        throw new PleoTransientException($"Pleo {endpoint}: hit the {PageSafetyCap}-page safety cap.");
    }

    private async Task<JsonElement> SendAsync(string endpoint, Func<HttpRequestMessage> make, CancellationToken ct)
    {
        using var req = make();
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(_options.ApiKey + ":")));
        using var resp = await SendRawAsync(endpoint, req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new PleoPermanentException($"Pleo {endpoint}: expected a JSON object.");
            return doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new PleoPermanentException($"Pleo {endpoint}: unreadable body.", ex);
        }
    }

    /// <summary>Returns only a success; the caller disposes it.</summary>
    private async Task<HttpResponseMessage> SendRawAsync(string endpoint, HttpRequestMessage req, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new PleoTransientException($"Pleo {endpoint}: send failed.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new PleoTransientException($"Pleo {endpoint}: timed out.", ex);
        }

        if (resp.IsSuccessStatusCode) return resp;

        using (resp)
        {
            var status = (int)resp.StatusCode;
            var label = $"Pleo {endpoint}: {status.ToString(CultureInfo.InvariantCulture)} {resp.ReasonPhrase}";
            if (status >= 500 || resp.StatusCode == HttpStatusCode.TooManyRequests)
                throw new PleoTransientException(label);
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new PleoPermanentException($"{label}: {PleoTextBounds.Bound(body, 300)}", status);
        }
    }
}
