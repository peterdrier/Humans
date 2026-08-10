using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Humans.Application.Extensions;
using Humans.Application.Interfaces.Holded;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Text;

namespace Humans.Infrastructure.Services.Holded;

public sealed class HoldedClient : IHoldedClient
{
    private const int DefaultRetryAfterSeconds = 5;
    private const int MaxRetryAfterSeconds = 60;

    // v2 ledger-entries dates arrive as DD/MM/YYYY, filtered on the *accounting* date; parsed to
    // Madrid midnight for a stable Instant.
    private static readonly DateTimeZone MadridZone = DateTimeZoneProviders.Tzdb["Europe/Madrid"];

    private readonly HttpClient _http;
    private readonly HoldedClientOptions _options;
    private readonly ILogger<HoldedClient> _logger;
    private readonly IHoldedCallLog _callLog;
    private readonly IClock _clock;

    public HoldedClient(
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

    public async Task<string> CreatePurchaseDocumentAsync(
        HoldedPurchaseDocumentInput input, CancellationToken ct = default)
    {
        var payload = new
        {
            contactId = input.ContactId, // TODO(probe): confirm field name (contactId vs contact)
            contactName = input.ContactName,
            date = input.Date.ToUnixTimeSeconds(),
            desc = input.Description,
            tags = input.Tags,
            items = input.Lines.Select(l => new
            {
                name = l.Description,
                units = 1,
                subtotal = l.Amount,
                tags = l.Tags
            })
        };

        using var req = new HttpRequestMessage(HttpMethod.Post,
            "/api/invoicing/v1/documents/purchase")
        { Content = JsonContent.Create(payload) };
        AttachAuth(req);

        using var resp = await SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        var node = JsonNode.Parse(body)
            ?? throw new HoldedTransientException("Holded returned empty body");
        var id = node["id"]?.GetValue<string>()
            ?? throw new HoldedTransientException("Holded response missing id");
        return id;
    }

    public async Task UpdatePurchaseDocumentTagsAsync(
        string documentId, IReadOnlyList<string> tags, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put,
            $"/api/invoicing/v1/documents/purchase/{documentId}")
        { Content = JsonContent.Create(new { tags }) };
        AttachAuth(req);

        using var resp = await SendAsync(req, ct);
    }

    public async Task UploadAttachmentAsync(
        string documentId, HoldedAttachmentInput attachment, CancellationToken ct = default)
    {
        using var content = new MultipartFormDataContent();
        var streamContent = new StreamContent(attachment.Content);
        streamContent.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue(attachment.ContentType);
        content.Add(streamContent, "file", attachment.FileName);

        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"/api/invoicing/v1/documents/purchase/{documentId}/attach")
        { Content = content };
        AttachAuth(req);

        using var resp = await SendAsync(req, ct);
    }

    public async Task<HoldedPurchaseDocumentDto> GetPurchaseDocumentAsync(
        string documentId, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"/api/invoicing/v1/documents/purchase/{documentId}");
        AttachAuth(req);

        using var resp = await SendAsync(req, ct);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var node = await JsonNode.ParseAsync(stream, cancellationToken: ct)
            ?? throw new HoldedTransientException("Holded returned empty body");

        return new HoldedPurchaseDocumentDto
        {
            Id = node["id"]?.GetValue<string>() ?? "",
            DocNumber = node["docNumber"]?.GetValue<string>() ?? "",
            Subtotal = ReadDecimal(node["subtotal"]),
            Tax = ReadDecimal(node["tax"]),
            Total = ReadDecimal(node["total"]),
            PaymentsTotal = ReadDecimal(node["paymentsTotal"]),
            PaymentsPending = ReadDecimal(node["paymentsPending"]),
            ApprovedAt = ReadInstant(node["approvedAt"]),
            Tags = node["tags"]?.AsArray()
                .Select(n => n!.GetValue<string>())
                .ToList() ?? []
        };
    }

    public async Task<IReadOnlyList<HoldedExpenseAccountDto>> ListExpenseAccountsAsync(
        CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/invoicing/v1/expensesaccounts");
        AttachAuth(req);
        using var resp = await SendAsync(req, ct);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var arr = (await JsonNode.ParseAsync(stream, cancellationToken: ct))?.AsArray() ?? [];
        return arr.Select(n => new HoldedExpenseAccountDto
        {
            Id = n!["id"]?.GetValue<string>() ?? "",
            AccountNum = (int)(n["accountNum"]?.GetValue<long>() ?? 0),
            Name = n["name"]?.GetValue<string>() ?? "",
        }).ToList();
    }

    public async Task<string> CreateExpenseAccountAsync(
        int accountNum, string name, CancellationToken ct = default)
    {
        // TODO(probe): confirm create-expenses-account payload field names against live API
        var payload = new { name, accountNum };
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/invoicing/v1/expensesaccounts")
        { Content = JsonContent.Create(payload) };
        AttachAuth(req);
        using var resp = await SendAsync(req, ct);
        var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))
            ?? throw new HoldedTransientException("Holded returned empty body");
        return node["id"]?.GetValue<string>()
            ?? throw new HoldedTransientException("Holded create-account response missing id");
    }

    public async Task<IReadOnlyList<HoldedPurchaseDocListItemDto>> ListPurchaseDocumentsPageAsync(
        int page, int limit, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"/api/invoicing/v1/documents/purchase?page={page}&limit={limit}");
        AttachAuth(req);
        using var resp = await SendAsync(req, ct);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        try
        {
            var arr = (await JsonNode.ParseAsync(stream, cancellationToken: ct))?.AsArray() ?? [];
            return arr.Select(ParsePurchaseDoc).ToList();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException
            or FormatException or OverflowException)
        {
            // Same normalization as GetContactAsync: a value of an unexpected type belongs to the
            // stored document, not to this request, so retrying cannot help. Raw parse throws would
            // escape past IHoldedClient's two typed exceptions and leak an Infrastructure detail into
            // the Application layer. Unlike the per-contact skip in ListContactsAsync this fails the
            // page rather than dropping the doc — these totals become budget-category actuals, and a
            // silently short page is wrong money rather than a missing name.
            throw new HoldedPermanentException(
                $"Holded purchase-document page {page} could not be read.", ex);
        }
    }

    public async Task<string> UpsertContactAsync(HoldedContactInput input, CancellationToken ct = default)
    {
        // TODO(probe): confirm contact payload field names (name/tradeName/customId/type/iban) against live API.
        var payload = new
        {
            name = input.Name,
            tradeName = input.TradeName,
            customId = input.CustomId,
            type = input.Type,
            iban = input.Iban,
        };

        var isUpdate = !string.IsNullOrEmpty(input.ExistingContactId);
        using var req = new HttpRequestMessage(
            isUpdate ? HttpMethod.Put : HttpMethod.Post,
            isUpdate
                ? $"/api/invoicing/v1/contacts/{input.ExistingContactId}"
                : "/api/invoicing/v1/contacts")
        { Content = JsonContent.Create(payload) };
        AttachAuth(req);

        using var resp = await SendAsync(req, ct);
        var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))
            ?? throw new HoldedTransientException("Holded returned empty body");
        return node["id"]?.GetValue<string>()
            ?? input.ExistingContactId
            ?? throw new HoldedTransientException("Holded contact upsert response missing id");
    }

    public async Task<HoldedContactDto> GetContactAsync(string contactId, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/invoicing/v1/contacts/{contactId}");
        AttachAuth(req);
        using var resp = await SendAsync(req, ct);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        try
        {
            var node = await JsonNode.ParseAsync(stream, cancellationToken: ct)
                ?? throw new HoldedTransientException("Holded returned empty body");

            return ParseContact(node, contactId);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException
            or FormatException or OverflowException)
        {
            // The single-contact sibling of the per-contact skip in ListContactsAsync. A value of an
            // unexpected type is a property of the stored contact, not of this request, so retrying
            // cannot help — surface it as permanent so callers that already handle the client's typed
            // exceptions degrade instead of letting a raw parse failure escape the client. The only
            // caller (ExpenseReportService.ProcessHoldedCreateAsync) would otherwise abort the whole
            // outbox batch and leave its own event neither processed nor failed.
            throw new HoldedPermanentException(
                $"Holded contact {contactId} could not be read.", ex);
        }
    }

    public async Task<IReadOnlyList<HoldedContactDto>> ListContactsAsync(CancellationToken ct = default)
    {
        // Paginates by walking `page` until an empty page comes back — the same
        // "empty list = past the end" contract already verified for this API family
        // via ListPurchaseDocumentsPageAsync. No `limit` param: unlike dailyledger/
        // documents-purchase, the contacts endpoint's page size has never been probed
        // live, so we don't assume it honors a requested limit.
        const int pageSafetyCap = 50; // 5 000+ contacts — far above a small nonprofit's vendor/member list
        var contacts = new List<HoldedContactDto>();
        var skipped = 0;
        var page = 1;
        for (; page <= pageSafetyCap; page++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/invoicing/v1/contacts?page={page}");
            AttachAuth(req);
            using var resp = await SendAsync(req, ct);
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            var arr = (await JsonNode.ParseAsync(stream, cancellationToken: ct))?.AsArray() ?? [];
            if (arr.Count == 0) break;

            foreach (var n in arr)
            {
                if (n is null) continue;
                try
                {
                    contacts.Add(ParseContact(n));
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException
                    or FormatException or OverflowException)
                {
                    // One contact carrying an unexpected value must not cost every other contact its name.
                    // This list is the sole source of creditor account names on /Finance/Creditors and the
                    // bind dropdown, and its caller degrades a throw to *all* names blank — which is how a
                    // single contact silently emptied the whole card (nobodies-collective/Humans#994).
                    // Detail on the first one only; the rest are counted, so a bad page cannot flood the log.
                    if (skipped == 0)
                        _logger.LogWarning(ex, "Unreadable Holded contact on page {Page}; skipping it.", page);
                    skipped++;
                }
            }
        }

        if (page > pageSafetyCap)
            _logger.LogWarning(
                "Holded contacts hit the {Cap}-page safety cap; results may be truncated.",
                pageSafetyCap);
        if (skipped > 0)
            _logger.LogWarning(
                "Skipped {Skipped} unreadable Holded contact(s); those accounts will show without a name.",
                skipped);
        return contacts;
    }

    /// <summary>Projects one Holded contact. Holded sends an absent sub-record as an empty array rather
    /// than null, and <see cref="JsonNode"/>'s string indexer throws on anything but a JsonObject — so
    /// every nested read goes through <see cref="Prop"/>, never the raw indexer.</summary>
    private static HoldedContactDto ParseContact(JsonNode node, string? fallbackId = null) => new()
    {
        Id = Prop(node, "id")?.GetValue<string>() ?? fallbackId ?? "",
        Name = Prop(node, "name")?.GetValue<string>(),
        SupplierAccountNum = ReadInt(Prop(Prop(node, "supplierRecord"), "num")),
    };

    /// <summary>A property of <paramref name="node"/>, or null when it is not an object. The raw
    /// <c>node["x"]</c> indexer throws InvalidOperationException on a non-object, which for a list read
    /// costs the whole page rather than the one field.</summary>
    private static JsonNode? Prop(JsonNode? node, string name) =>
        node is JsonObject obj ? obj[name] : null;

    /// <summary>The array at <paramref name="node"/>, or empty when it is absent or of another kind.
    /// <see cref="JsonNode.AsArray"/> throws on a non-array for the same reason the string indexer
    /// throws on a non-object, and Holded is equally happy to send an absent collection as a scalar.</summary>
    private static JsonArray Arr(JsonNode? node) => node as JsonArray ?? [];

    public async Task<IReadOnlyList<HoldedLedgerLineDto>> ListLedgerEntriesAsync(
        LocalDate from, LocalDate to, int? accountNum = null, CancellationToken ct = default)
    {
        const int pageSafetyCap = 100; // 20 000 lines/window — far above a small nonprofit's volume
        var query =
            $"/api/v2/ledger-entries?start_date={LocalDatePattern.Iso.Format(from)}" +
            $"&end_date={LocalDatePattern.Iso.Format(to)}&limit=200";
        if (accountNum is { } num)
            query += $"&account={num}";

        var items = await GetPagedAsync(query, pageSafetyCap, ct);
        try
        {
            return items.Select(n => new HoldedLedgerLineDto
            {
                EntryNumber = ReadInt(Prop(n, "entry_number")) ?? 0,
                Line = ReadInt(Prop(n, "line")) ?? 0,
                Date = ParseLedgerDate(Prop(n, "date")?.GetValue<string>() ?? ""),
                AccountNum = ReadInt(Prop(n, "account")) ?? 0,
                Debit = ReadDecimalV2(Prop(n, "debit")),
                Credit = ReadDecimalV2(Prop(n, "credit")),
                Type = Prop(n, "type")?.GetValue<string>(),
                Description = Prop(n, "description")?.GetValue<string>(),
            }).ToList();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException
            or FormatException or OverflowException or UnparsableValueException)
        {
            // As in ListPurchaseDocumentsPageAsync — permanent, and the whole page fails rather
            // than skipping the line. Creditor and account balances are summed from these debits
            // and credits, so a quietly dropped line reads as a settled entry that never happened.
            throw new HoldedPermanentException(
                $"Holded ledger-entries {from}..{to} could not be read.", ex);
        }
    }

    public async Task<IReadOnlyList<HoldedAccountDto>> ListAccountingAccountsAsync(
        CancellationToken ct = default)
    {
        const int pageSafetyCap = 5; // 267 accounts today, unpaginated — plenty of headroom
        var items = await GetPagedAsync("/api/v2/accounting-accounts?limit=200", pageSafetyCap, ct);
        try
        {
            return items.Select(n => new HoldedAccountDto
            {
                Id = Prop(n, "id")?.GetValue<string>() ?? "",
                Number = ReadInt(Prop(n, "number")) ?? 0,
                Name = Prop(n, "name")?.GetValue<string>() ?? "",
                Group = Prop(n, "group")?.GetValue<string>(),
                Debit = ReadDecimalV2(Prop(n, "debit")),
                Credit = ReadDecimalV2(Prop(n, "credit")),
                Balance = ReadDecimalV2(Prop(n, "balance")),
                Archived = Prop(n, "archived")?.GetValue<bool>() ?? false,
            }).ToList();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException
            or FormatException or OverflowException)
        {
            throw new HoldedPermanentException("Holded accounting-accounts could not be read.", ex);
        }
    }

    public async Task<HoldedUsageDto> GetUsageAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/v2/usage");
        AttachAuth(req);
        using var resp = await SendAsync(req, ct);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var node = await JsonNode.ParseAsync(stream, cancellationToken: ct)
            ?? throw new HoldedTransientException("Holded returned empty body");
        try
        {
            var secondary = Prop(node, "secondary_usages") as JsonObject;
            return new HoldedUsageDto
            {
                Period = Prop(node, "period")?.GetValue<string>() ?? "",
                Usage = Prop(node, "usage")?.GetValue<long>() ?? 0,
                Limit = Prop(node, "limit")?.GetValue<long>() ?? 0,
                SecondaryUsages = secondary is null
                    ? new Dictionary<string, long>(StringComparer.Ordinal)
                    : secondary.ToDictionary(
                        kv => kv.Key, kv => kv.Value?.GetValue<long>() ?? 0, StringComparer.Ordinal),
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException
            or FormatException or OverflowException)
        {
            throw new HoldedPermanentException("Holded usage could not be read.", ex);
        }
    }

    /// <summary>Walks a v2 cursor-paginated collection: follows `cursor` while `has_more`, collecting
    /// `items` elements. Caps at pageSafetyCap pages and logs when hit (no silent caps).</summary>
    private async Task<List<JsonNode>> GetPagedAsync(
        string pathAndQuery, int pageSafetyCap, CancellationToken ct)
    {
        var items = new List<JsonNode>();
        string? cursor = null;
        for (var page = 1; page <= pageSafetyCap; page++)
        {
            var url = cursor is null
                ? pathAndQuery
                : $"{pathAndQuery}&cursor={Uri.EscapeDataString(cursor)}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            AttachAuth(req);
            using var resp = await SendAsync(req, ct);
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            var root = await JsonNode.ParseAsync(stream, cancellationToken: ct);
            foreach (var n in Arr(Prop(root, "items")))
                if (n is not null) items.Add(n);

            var hasMore = Prop(root, "has_more")?.GetValue<bool>() ?? false;
            cursor = Prop(root, "cursor")?.GetValue<string>();
            if (!hasMore || cursor is null) return items;
        }
        _logger.LogWarning(
            "Holded cursor pagination hit the {Cap}-page safety cap for {PathAndQuery}; results may be truncated.",
            pageSafetyCap, pathAndQuery);
        return items;
    }

    private static Instant ParseLedgerDate(string s) =>
        DateFormattingExtensions.HoldedLedgerDatePattern.Parse(s).Value
            .AtStartOfDayInZone(MadridZone).ToInstant();

    /// <summary>Projects one purchase document. Every container read goes through <see cref="Prop"/> /
    /// <see cref="Arr"/> rather than the raw indexer, for the reason spelled out on those two.</summary>
    private static HoldedPurchaseDocListItemDto ParsePurchaseDoc(JsonNode? n) => new()
    {
        Id = Prop(n, "id")?.GetValue<string>() ?? "",
        DocNumber = Prop(n, "docNumber")?.GetValue<string>() ?? "",
        ContactName = Prop(n, "contactName")?.GetValue<string>() ?? "",
        Date = ReadInstant(Prop(n, "date")) ?? Instant.FromUnixTimeSeconds(0),
        Subtotal = ReadDecimal(Prop(n, "subtotal")),
        Tax = ReadDecimal(Prop(n, "tax")),
        Total = ReadDecimal(Prop(n, "total")),
        ApprovedAt = ReadInstant(Prop(n, "approvedAt")),
        Currency = Prop(n, "currency")?.GetValue<string>() ?? "eur",
        Tags = ReadTags(Prop(n, "tags")),
        Lines = Arr(Prop(n, "products")).Select(p => new HoldedPurchaseLineDto
        {
            Amount = ReadDecimal(Prop(p, "price")),
            AccountId = Prop(p, "account")?.GetValue<string>(),
            Tags = ReadTags(Prop(p, "tags")),
        }).ToList(),
    };

    private static IReadOnlyList<string> ReadTags(JsonNode? node) =>
        Arr(node).Where(t => t is not null).Select(t => t!.GetValue<string>()).ToList();

    private void AttachAuth(HttpRequestMessage req) =>
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage req, CancellationToken ct,
        [CallerMemberName] string caller = "")
    {
        using var _ = _logger.TimeOperation(operation: caller);
        var resp = await SendOnceAsync(req, caller, ct);

        // 429 is retried once, only for content-free (GET) requests — a content-bearing request
        // (POST/PUT) is not safely repeatable without knowing whether Holded already applied it.
        if (resp.StatusCode == HttpStatusCode.TooManyRequests && req.Content is null)
        {
            var retryAfterSeconds = Math.Min(
                ReadRetryAfterSeconds(resp) ?? DefaultRetryAfterSeconds, MaxRetryAfterSeconds);
            resp.Dispose();
            await Task.Delay(TimeSpan.FromSeconds(retryAfterSeconds), ct);
            resp = await SendOnceAsync(CloneForRetry(req), caller, ct);
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
            throw new HoldedPermanentException((int)resp.StatusCode, body,
                $"Holded {(int)resp.StatusCode} {resp.ReasonPhrase}: {body}");
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

    private static int? ReadRetryAfterSeconds(HttpResponseMessage resp) =>
        resp.Headers.RetryAfter?.Delta is { } delta ? (int)delta.TotalSeconds : null;

    /// <summary>A GET <see cref="HttpRequestMessage"/> can only be sent once through <see cref="HttpClient"/>;
    /// the 429 retry needs a fresh instance carrying the same method, URI, and headers (including auth).</summary>
    private static HttpRequestMessage CloneForRetry(HttpRequestMessage req)
    {
        var clone = new HttpRequestMessage(req.Method, req.RequestUri);
        foreach (var header in req.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return clone;
    }

    private static decimal ReadDecimal(JsonNode? node) =>
        node?.GetValue<decimal>() ?? 0m;

    // v2 endpoints send decimals as strings (e.g. "121.00"); v1 endpoints (not yet migrated) still
    // send numeric JSON tokens via ReadDecimal above.
    private static decimal ReadDecimalV2(JsonNode? node) =>
        decimal.Parse(node?.GetValue<string>() ?? "0", CultureInfo.InvariantCulture);

    // GetValue<decimal> (not <long>) so a JSON float token like 40000001.0 parses; cast truncates.
    private static int? ReadInt(JsonNode? node) =>
        node is null ? null : (int?)node.GetValue<decimal>();

    private static Instant? ReadInstant(JsonNode? node)
    {
        if (node is null) return null;
        var seconds = node.GetValue<long>();
        return seconds == 0 ? null : Instant.FromUnixTimeSeconds(seconds);
    }
}
