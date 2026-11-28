using Humans.Base.Http;
using Humans.Pleo.Contracts;
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

internal sealed class PleoClient(HttpClient http, IOptions<PleoClientOptions> options)
    : ApiClientBase(http, options.Value.BaseUrl), IPleoClient
{
    private const int PageLimit = 100;

    /// <summary>Throws rather than returning a prefix: the sweep deletes whatever a fetch leaves out.</summary>
    private const int PageSafetyCap = 100;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly PleoClientOptions _options = options.Value;

    protected override string ApiName => "Pleo";

    protected override int ErrorBodyLength => 300;

    protected override Exception Transient(string message, Exception? inner = null) =>
        inner is null ? new PleoTransientException(message) : new PleoTransientException(message, inner);

    protected override Exception Permanent(string message, HttpStatusCode? status = null, string? body = null) =>
        status is { } code ? new PleoPermanentException(message, (int)code) : new PleoPermanentException(message);

    protected override void Authorize(HttpRequestMessage request) =>
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(_options.ApiKey + ":")));

    public bool IsConfigured => HasBaseAddress && MissingSettings is null;

    public string? MissingSettings =>
        (string.IsNullOrWhiteSpace(_options.ApiKey), string.IsNullOrWhiteSpace(_options.CompanyId)) switch
        {
            (true, true) => "PLEO_API_KEY and PLEO_COMPANY_ID",
            (true, false) => "PLEO_API_KEY",
            (false, true) => "PLEO_COMPANY_ID",
            _ => null
        };

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

    public async Task<IReadOnlyList<PleoCategoryWire>> SearchCategoriesAsync(CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new { companyId = _options.CompanyId });
        var items = await GetAllPagesAsync("bookkeeping-categories:search", after =>
            Post($"/v1/bookkeeping-categories:search?limit={PageLimit}{After(after)}", body), ct);
        return items.Select(Read<PleoCategoryWire>).ToList();
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
        var root = await SendJsonAsync("balance",
            new HttpRequestMessage(HttpMethod.Get, $"/v1/companies/{Company}/balance"), ct);
        try
        {
            var accounts = root.GetProperty("data").GetProperty("accounts").EnumerateArray().ToList();
            var primary = accounts.FirstOrDefault(a =>
                a.TryGetProperty("isPrimary", out var p) && p.ValueKind == JsonValueKind.True);
            if (primary.ValueKind == JsonValueKind.Undefined)
                throw new PleoPermanentException($"Pleo balance: no primary account among {accounts.Count}.");
            var balance = primary.GetProperty("balance");
            var current = balance.GetProperty("current");
            var currency = current.GetProperty("currency").GetString();
            if (string.IsNullOrWhiteSpace(currency))
                throw new PleoPermanentException("Pleo balance: primary account currency is missing.");
            return new PleoBalanceWire(
                currency,
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
        using var resp = await SendAsync(req, ct, "receipt file");
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
        if (item.ValueKind != JsonValueKind.Object
            || !item.TryGetProperty("id", out var id)
            || id.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(id.GetString()))
            throw new PleoPermanentException($"Pleo returned a {typeof(T).Name} without a usable id.");

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
        await foreach (var page in GetPagesAsync(endpoint, cursor => ReadPageAsync(endpoint, request(cursor), ct), PageSafetyCap, ct))
            items.AddRange(page);
        return items;
    }

    private async Task<(IEnumerable<JsonElement> Page, string? Next)> ReadPageAsync(
        string endpoint, HttpRequestMessage request, CancellationToken ct)
    {
        var root = await SendJsonAsync(endpoint, request, ct);

        // Strict: a body without data would read as an empty page, and an empty sweep deletes the mirror.
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new PleoTransientException($"Pleo {endpoint}: a 200 without a data array.");

        if (!root.TryGetProperty("pagination", out var pagination)
            || pagination.ValueKind != JsonValueKind.Object
            || !pagination.TryGetProperty("hasNextPage", out var next)
            || next.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new PleoTransientException($"Pleo {endpoint}: a page without a boolean hasNextPage.");
        if (next.ValueKind == JsonValueKind.False) return (data.EnumerateArray().ToList(), null);

        var after = pagination.TryGetProperty("endCursor", out var end) && end.ValueKind == JsonValueKind.String
            ? end.GetString()
            : null;
        if (string.IsNullOrEmpty(after))
            throw new PleoTransientException($"Pleo {endpoint}: hasNextPage without an endCursor.");
        return (data.EnumerateArray().ToList(), after);
    }

    private async Task<JsonElement> SendJsonAsync(string endpoint, HttpRequestMessage request, CancellationToken ct)
    {
        using var req = request;
        if (MissingSettings is { } missing)
            throw new PleoPermanentException($"{missing} not set.");

        using var resp = await SendAsync(req, ct, endpoint);
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
}
