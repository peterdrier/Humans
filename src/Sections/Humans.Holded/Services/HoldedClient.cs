using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Humans.Base.Extensions;
using Humans.Base.Helpers;
using Humans.Holded.Contracts;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Text;

namespace Humans.Holded.Services;

internal sealed class HoldedClient : IHoldedClient
{
    private const int DefaultRetryAfterSeconds = 5;
    private const int MaxRetryAfterSeconds = 60;

    /// <summary>Every outbound payload omits its null members instead of sending them. Holded applies
    /// each key a POST/PUT carries, so <c>"code":null</c> on a contact update blanks the tax id
    /// already stored against that contact — and a creditor update supplies only the handful of
    /// fields it means to change. Omission is "leave it alone", which is what every caller means;
    /// nothing here ever clears a Holded field on purpose.</summary>
    private static readonly JsonSerializerOptions OmitNulls =
        new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

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

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.ApiKey);

    public async Task<string> CreatePurchaseDocumentAsync(
        HoldedPurchaseDocumentInput input, CancellationToken ct = default)
    {
        var payload = new
        {
            contact_id = input.ContactId,
            contact_name = input.ContactName,
            date = LocalDatePattern.Iso.Format(input.Date.InZone(MadridZone).Date),
            description = input.Description,
            // Tags are dead on the write side (Peter, 2026-08-10): they were a v1 workaround from
            // before double-entry was understood. items[].account books the doc to the right
            // department directly, so no tag/retag round-trip is ever needed.
            items = input.Lines.Select(l => new
            {
                name = l.Description,
                units = 1,
                price = l.Amount,
                account = l.AccountId,
            })
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v2/purchases")
        { Content = JsonContent.Create(payload, options: OmitNulls) };
        AttachAuth(req);

        using var resp = await SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        var node = JsonNode.Parse(body)
            ?? throw new HoldedTransientException("Holded returned empty body");
        var id = node["id"]?.GetValue<string>()
            ?? throw new HoldedTransientException("Holded response missing id");
        return id;
    }

    public async Task UploadAttachmentAsync(
        string documentId, HoldedAttachmentInput attachment, CancellationToken ct = default)
    {
        using var content = new MultipartFormDataContent();
        var streamContent = new StreamContent(attachment.Content);
        streamContent.Headers.ContentType =
            new MediaTypeHeaderValue(attachment.ContentType);
        content.Add(streamContent, "file", attachment.FileName);

        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"/api/v2/purchases/{documentId}/attachments")
        { Content = content };
        AttachAuth(req);

        using var resp = await SendAsync(req, ct);
    }

    public async Task<HoldedPurchaseDocumentDto> GetPurchaseDocumentAsync(
        string documentId, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v2/purchases/{documentId}");
        AttachAuth(req);

        using var resp = await SendAsync(req, ct);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        try
        {
            var node = await JsonNode.ParseAsync(stream, cancellationToken: ct)
                ?? throw new HoldedTransientException("Holded returned empty body");

            return new HoldedPurchaseDocumentDto
            {
                Id = Prop(node, "id")?.GetValue<string>() ?? "",
                DocNumber = Prop(node, "document_number")?.GetValue<string>() ?? "",
                Subtotal = ReadDecimalV2(Prop(node, "subtotal")),
                Tax = ReadDecimalV2(Prop(node, "tax")),
                Total = ReadDecimalV2(Prop(node, "total")),
                PaymentsTotal = ReadDecimalV2(Prop(node, "payments_total")),
                PaymentsPending = ReadDecimalV2(Prop(node, "payments_pending")),
                ApprovedAt = ParseApprovedAt(Prop(node, "approved_at")?.GetValue<string>()),
                Tags = ReadTags(Prop(node, "tags")),
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException
            or FormatException or OverflowException or UnparsableValueException)
        {
            // Same normalization as GetContactAsync: a value of an unexpected type belongs to the
            // stored document, not to this request, so retrying cannot help. UnparsableValueException
            // covers ParseApprovedAt's NodaTime .Value on a non-empty but malformed timestamp.
            throw new HoldedPermanentException(
                $"Holded purchase document {documentId} could not be read.", ex);
        }
    }

    public async Task ApprovePurchaseDocumentAsync(string documentId, CancellationToken ct = default)
    {
        // POST with no body 411s through Holded's proxy without an explicit Content-Length — an
        // empty content sets one, where leaving Content null (as ApproveSalesDocumentAsync does)
        // does not.
        using var req = new HttpRequestMessage(
            HttpMethod.Post, $"/api/v2/purchases/{documentId}/approve")
        { Content = new ByteArrayContent([]) };
        AttachAuth(req);
        using var resp = await SendAsync(req, ct);
    }

    public async Task<IReadOnlyList<HoldedExpenseAccountDto>> ListExpenseAccountsAsync(
        CancellationToken ct = default)
    {
        const int pageSafetyCap = 5; // a handful of expense accounts today, unpaginated in practice
        try
        {
            var items = await GetPagedAsync("/api/v2/expenses-accounts?limit=200", pageSafetyCap, ct);
            return items.Select(n => new HoldedExpenseAccountDto
            {
                Id = Prop(n, "id")?.GetValue<string>() ?? "",
                AccountNum = ReadInt(Prop(n, "account_num")) ?? 0,
                Name = Prop(n, "name")?.GetValue<string>() ?? "",
            }).ToList();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException
            or FormatException or OverflowException)
        {
            throw new HoldedPermanentException("Holded expense accounts could not be read.", ex);
        }
    }

    public async Task<string> CreateExpenseAccountAsync(
        int accountNum, string name, CancellationToken ct = default)
    {
        var payload = new { name, account_num = accountNum };
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v2/expenses-accounts")
        { Content = JsonContent.Create(payload, options: OmitNulls) };
        AttachAuth(req);
        using var resp = await SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        try
        {
            var node = JsonNode.Parse(body)
                ?? throw new HoldedTransientException("Holded returned empty body");
            return node["id"]?.GetValue<string>()
                ?? throw new HoldedTransientException("Holded create-account response missing id");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException
            or FormatException or OverflowException)
        {
            throw new HoldedPermanentException(
                "Holded create-account response could not be read.", ex);
        }
    }

    public async Task<IReadOnlyList<HoldedPurchaseDocListItemDto>> ListPurchaseDocumentsAsync(
        CancellationToken ct = default)
    {
        const int pageSafetyCap = 200; // 40 000 docs — far above a small nonprofit's volume
        var items = await GetPagedAsync("/api/v2/purchases?limit=200", pageSafetyCap, ct);
        try
        {
            return items.Select(ParsePurchaseDoc).ToList();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException
            or FormatException or OverflowException)
        {
            // Same normalization as GetContactAsync: a value of an unexpected type belongs to the
            // stored document, not to this request, so retrying cannot help. Raw parse throws would
            // escape past IHoldedClient's two typed exceptions and leak an Infrastructure detail into
            // the Application layer. Unlike the per-contact skip in ListContactsAsync this fails the
            // whole list rather than dropping the doc — these totals become budget-category actuals,
            // and a silently short list is wrong money rather than a missing name.
            throw new HoldedPermanentException("Holded purchase documents could not be read.", ex);
        }
    }

    public async Task<string> UpsertContactAsync(HoldedContactInput input, CancellationToken ct = default)
    {
        // v2 contacts POST/PUT have no `custom_id` field (see HoldedContactInput.CustomId) — not sent.
        // bill_address stays null when we hold no address parts, and OmitNulls then drops the key:
        // an object of nulls, or the key with a null value, would both blank an address already on
        // the contact.
        object? billAddress =
            string.IsNullOrWhiteSpace(input.Address) && string.IsNullOrWhiteSpace(input.CountryCode)
                ? null
                : new { address = input.Address, country_code = input.CountryCode };

        var isUpdate = !string.IsNullOrEmpty(input.ExistingContactId);

        // On update, `type` is omitted rather than sent (same OmitNulls trick as bill_address): a
        // PUT carrying it flips a manually-created contact's type (e.g. a proveedor kept as
        // "supplier" for a 400000xx account) to whatever Type defaults to, which mints a new
        // 410000xx creditor account instead of reusing the existing one. Only a create needs to
        // set a type at all.
        var payload = new
        {
            name = input.Name,
            trade_name = input.TradeName,
            type = isUpdate ? null : input.Type,
            iban = input.Iban,
            code = input.TaxCode,
            email = input.Email,
            bill_address = billAddress,
        };
        using var req = new HttpRequestMessage(
            isUpdate ? HttpMethod.Put : HttpMethod.Post,
            isUpdate
                ? $"/api/v2/contacts/{input.ExistingContactId}"
                : "/api/v2/contacts")
        { Content = JsonContent.Create(payload, options: OmitNulls) };
        AttachAuth(req);

        using var resp = await SendAsync(req, ct);
        var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))
            ?? throw new HoldedTransientException("Holded returned empty body");
        return node["id"]?.GetValue<string>()
            ?? input.ExistingContactId
            ?? throw new HoldedTransientException("Holded contact upsert response missing id");
    }

    /// <summary>The v2 path segment for a sales-document kind. Both kinds share the same payload
    /// shape and the same create/approve/read pipeline; only this segment differs.</summary>
    private static string SalesSegment(HoldedSalesDocumentKind kind) => kind switch
    {
        HoldedSalesDocumentKind.Invoice => "invoices",
        HoldedSalesDocumentKind.SalesReceipt => "sales-receipts",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown Holded sales-document kind."),
    };

    public async Task<string> CreateSalesDocumentAsync(
        HoldedSalesDocumentKind kind, HoldedSalesDocumentInput input, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(
            HttpMethod.Post, $"/api/v2/{SalesSegment(kind)}")
        { Content = JsonContent.Create(BuildSalesDocumentPayload(input), options: OmitNulls) };
        AttachAuth(req);

        using var resp = await SendAsync(req, ct);
        var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))
            ?? throw new HoldedTransientException("Holded returned empty body");
        return Prop(node, "id")?.GetValue<string>()
            ?? throw new HoldedTransientException("Holded sales-document response missing id");
    }

    public async Task ApproveSalesDocumentAsync(
        HoldedSalesDocumentKind kind, string documentId, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(
            HttpMethod.Post, $"/api/v2/{SalesSegment(kind)}/{documentId}/approve");
        AttachAuth(req);
        using var resp = await SendAsync(req, ct);
    }

    public async Task<IReadOnlyList<string>> FindSalesDocumentIdsByTagAsync(
        HoldedSalesDocumentKind kind, string tag, CancellationToken ct = default)
    {
        const int pageSafetyCap = 200; // 40 000 documents — far above a small nonprofit's volume
        var items = await GetPagedAsync($"/api/v2/{SalesSegment(kind)}?limit=200", pageSafetyCap, ct);
        try
        {
            return items
                .Where(n => ReadTags(Prop(n, "tags")).Contains(tag, StringComparer.Ordinal))
                // A match without an id cannot be dropped: the caller reads an empty result as
                // "nothing issued yet" and creates a second approved document.
                .Select(n => Prop(n, "id")?.GetValue<string>() is { Length: > 0 } id
                    ? id
                    : throw new HoldedPermanentException(
                        "Holded sales document is missing required field 'id' — refusing the page."))
                .ToList();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException
            or FormatException or OverflowException)
        {
            // Same normalization as ListPurchaseDocumentsAsync: the value that failed to parse
            // belongs to the stored document, not to this request, so a retry cannot help.
            throw new HoldedPermanentException(
                $"Holded {SalesSegment(kind)} documents could not be searched.", ex);
        }
    }

    public async Task<HoldedSalesDocumentDto> GetSalesDocumentAsync(
        HoldedSalesDocumentKind kind, string documentId, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(
            HttpMethod.Get, $"/api/v2/{SalesSegment(kind)}/{documentId}");
        AttachAuth(req);
        using var resp = await SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        try
        {
            var node = JsonNode.Parse(body)
                ?? throw new HoldedTransientException("Holded returned empty body");
            return new HoldedSalesDocumentDto
            {
                Id = Prop(node, "id")?.GetValue<string>() ?? documentId,
                DocNumber = Prop(node, "document_number")?.GetValue<string>() ?? "",
                Subtotal = ReadDecimalV2(Prop(node, "subtotal")),
                Tax = ReadDecimalV2(Prop(node, "tax")),
                Total = ReadDecimalV2(Prop(node, "total")),
                Status = Prop(node, "status")?.GetValue<string>(),
                IsDraft = Prop(node, "draft")?.GetValue<bool>(),
                RawJson = body,
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException
            or FormatException or OverflowException)
        {
            // The doc is already created and approved at this point, so a retry would duplicate it.
            // Permanent is the honest classification: the value that failed to parse belongs to the
            // stored document, not to this request.
            throw new HoldedPermanentException(
                $"Holded {SalesSegment(kind)} document {documentId} could not be read.", ex);
        }
    }

    private static object BuildSalesDocumentPayload(HoldedSalesDocumentInput input) => new
    {
        contact_id = input.ContactId,
        contact_name = input.ContactName,
        date = LocalDatePattern.Iso.Format(input.Date.InZone(MadridZone).Date),
        description = input.Description,
        notes = input.Notes,
        tags = input.Tags,
        currency = "EUR",
        items = input.Lines.Select(l => new
        {
            name = l.Name,
            units = l.Units,
            price = l.Price,
            taxes = l.Taxes,
            account = l.AccountId,
        }).ToList(),
    };

    public async Task<HoldedContactDto> GetContactAsync(string contactId, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v2/contacts/{contactId}");
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
        const int pageSafetyCap = 50; // 10 000+ contacts (limit=200/page) — far above a small nonprofit's vendor/member list
        List<JsonNode> items;
        try
        {
            items = await GetPagedAsync("/api/v2/contacts?limit=200", pageSafetyCap, ct);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException
            or FormatException or OverflowException)
        {
            // The page envelope itself (not a per-contact value) was unreadable — that's not the
            // per-contact skip-and-log below (#994), which only covers one bad item in an otherwise
            // good page.
            throw new HoldedPermanentException("Holded contacts could not be read.", ex);
        }

        var contacts = new List<HoldedContactDto>();
        var skipped = 0;
        foreach (var n in items)
        {
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
                    _logger.LogWarning(ex, "Unreadable Holded contact; skipping it.");
                skipped++;
            }
        }

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
        SupplierAccountNum = ReadInt(Prop(Prop(node, "supplier_record"), "num")),
        TradeName = Prop(node, "trade_name")?.GetValue<string>(),
        Email = Prop(node, "email")?.GetValue<string>(),
        Phone = Prop(node, "phone")?.GetValue<string>(),
        Mobile = Prop(node, "mobile")?.GetValue<string>(),
        Iban = Prop(node, "iban")?.GetValue<string>(),
        TaxCode = Prop(node, "code")?.GetValue<string>(),
        Address = FormatAddress(Prop(node, "bill_address")),
    };

    /// <summary>One display string joined from bill_address's non-empty parts, or null when the
    /// address is absent/empty. Holded sends an absent bill_address as null (not an empty array like
    /// supplierRecord), but <see cref="Prop"/> is still the safe read for the same reason as elsewhere.</summary>
    private static string? FormatAddress(JsonNode? billAddress)
    {
        var parts = new[] { "address", "postal_code", "city", "province", "country" }
            .Select(k => Prop(billAddress, k)?.GetValue<string>())
            .Where(v => !string.IsNullOrWhiteSpace(v));
        var joined = string.Join(", ", parts);
        return joined.Length == 0 ? null : joined;
    }

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
        // end_date is exclusive on the live API — send to+1 so the inclusive-`to` contract holds.
        var query =
            $"/api/v2/ledger-entries?start_date={LocalDatePattern.Iso.Format(from)}" +
            $"&end_date={LocalDatePattern.Iso.Format(to.PlusDays(1))}&limit=200";
        if (accountNum is { } num)
            query += $"&account={num}";

        var items = await GetPagedAsync(query, pageSafetyCap, ct);
        try
        {
            return items.Select(n => new HoldedLedgerLineDto
            {
                // Identity fields must be present — a manufactured 0 key would survive into
                // ReplaceLedgerWindowAsync and evict the real cached row it fails to match.
                EntryNumber = ReadRequiredInt(Prop(n, "entry_number"), "entry_number"),
                Line = ReadRequiredInt(Prop(n, "line"), "line"),
                Date = ParseLedgerDate(Prop(n, "date")?.GetValue<string>() ?? ""),
                AccountNum = ReadRequiredInt(Prop(n, "account"), "account"),
                Debit = ReadRequiredDecimalV2(Prop(n, "debit"), "debit"),
                Credit = ReadRequiredDecimalV2(Prop(n, "credit"), "credit"),
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
        try
        {
            // Parse inside the try: a truncated 200 body must surface as a typed Holded
            // exception so /Holded degrades to "unreachable" instead of a raw 500.
            var node = await JsonNode.ParseAsync(stream, cancellationToken: ct)
                ?? throw new HoldedTransientException("Holded returned empty body");
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
    /// `items` elements. Throws when pageSafetyCap is hit rather than returning a truncated result —
    /// these lists feed replace-semantics reconciliation downstream, so a silently short fetch would
    /// drive deletion of rows that still exist in Holded (lossy becomes destructive).</summary>
    private async Task<List<JsonNode>> GetPagedAsync(
        string pathAndQuery, int pageSafetyCap, CancellationToken ct,
        [CallerMemberName] string caller = "")
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
            // Forward the real caller (ListLedgerEntriesAsync, ListContactsAsync, …) — SendAsync's own
            // [CallerMemberName] would otherwise record every paginated endpoint as "GetPagedAsync",
            // collapsing the call log's per-endpoint breakdown (used by the admin overview, Task 7).
            using var resp = await SendAsync(req, ct, caller);
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            var root = await JsonNode.ParseAsync(stream, cancellationToken: ct);

            // Strict envelope validation, not the forgiving Prop/Arr fallbacks: a 200 carrying
            // Holded's {"status":0,...} error object — or any body without an `items` array —
            // would otherwise read as a successfully-empty page, and list results feed
            // replace-semantics windows where a false empty deletes every cached row in range.
            if (Prop(root, "items") is not JsonArray itemsArr)
            {
                var preview = root?.ToJsonString() ?? "null";
                throw new HoldedTransientException(
                    $"Holded returned a 200 without a valid items array for {pathAndQuery.Split('?', 2)[0]} " +
                    $"(body starts: {preview[..Math.Min(preview.Length, 120)]}).");
            }
            foreach (var n in itemsArr)
                if (n is not null) items.Add(n);

            // Absent has_more is a legitimate final page — the live accounting-accounts
            // response carries items only, no pagination metadata. But has_more:true without
            // a cursor cannot be followed, and returning the prefix would feed replace
            // semantics a truncated list.
            var hasMore = Prop(root, "has_more")?.GetValue<bool>() ?? false;
            cursor = Prop(root, "cursor")?.GetValue<string>();
            if (!hasMore) return items;
            if (string.IsNullOrEmpty(cursor))
                throw new HoldedTransientException(
                    $"Holded page for {pathAndQuery.Split('?', 2)[0]} claims has_more but carries no cursor.");
        }

        var endpoint = pathAndQuery.Split('?', 2)[0];
        throw new HoldedTransientException(
            $"Holded cursor pagination for {endpoint} hit the {pageSafetyCap}-page safety cap.");
    }

    private static Instant ParseLedgerDate(string s) =>
        DateFormattingExtensions.HoldedLedgerDatePattern.Parse(s).Value
            .AtStartOfDayInZone(MadridZone).ToInstant();

    private static Instant ParseIsoDate(string s) =>
        LocalDatePattern.Iso.Parse(s).Value.AtStartOfDayInZone(MadridZone).ToInstant();

    /// <summary>`approved_at` arrives as an offset-less ISO date-time (e.g. "2024-01-15T13:00:00");
    /// treated as Madrid local time, consistent with every other Holded date/time on this client.</summary>
    private static Instant? ParseApprovedAt(string? s) =>
        string.IsNullOrEmpty(s)
            ? null
            : LocalDateTimePattern.GeneralIso.Parse(s).Value.InZoneLeniently(MadridZone).ToInstant();

    /// <summary>Projects one purchase document. Every container read goes through <see cref="Prop"/> /
    /// <see cref="Arr"/> rather than the raw indexer, for the reason spelled out on those two.</summary>
    private static HoldedPurchaseDocListItemDto ParsePurchaseDoc(JsonNode? n) => new()
    {
        Id = Prop(n, "id")?.GetValue<string>() ?? "",
        DocNumber = Prop(n, "document_number")?.GetValue<string>() ?? "",
        ContactName = Prop(n, "contact_name")?.GetValue<string>() ?? "",
        Date = ParseIsoDate(Prop(n, "date")?.GetValue<string>() ?? ""),
        Subtotal = ReadDecimalV2(Prop(n, "subtotal")),
        Tax = ReadDecimalV2(Prop(n, "tax")),
        // Total feeds the budget actuals; an absent field must fail the page, not upsert 0.00.
        Total = ReadRequiredDecimalV2(Prop(n, "total"), "total"),
        IsDraft = Prop(n, "draft")?.GetValue<bool>(),
        Currency = Prop(n, "currency")?.GetValue<string>() ?? "eur",
        Tags = ReadTags(Prop(n, "tags")),
        Lines = Arr(Prop(n, "lines")).Select(p => new HoldedPurchaseLineDto
        {
            Amount = ReadDecimalV2(Prop(p, "price")),
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
            // The message is the diagnostic that reaches logs, the outbox row's LastError, the audit
            // trail and the finance-admin UI. A 4xx on a contact upsert can echo the IBAN we just
            // sent straight back in the body, so mask before it leaves the client
            // (memory/code/iban-mask-in-logs.md). ResponseBody keeps the untouched body for any
            // caller that deliberately needs it.
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

    /// <summary>A GET <see cref="HttpRequestMessage"/> can only be sent once through <see cref="HttpClient"/>;
    /// the 429 retry needs a fresh instance carrying the same method, URI, and headers (including auth).</summary>
    private static HttpRequestMessage CloneForRetry(HttpRequestMessage req)
    {
        var clone = new HttpRequestMessage(req.Method, req.RequestUri);
        foreach (var header in req.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return clone;
    }

    // v2 sends decimals as strings (e.g. "121.00").
    private static decimal ReadDecimalV2(JsonNode? node) =>
        decimal.Parse(node?.GetValue<string>() ?? "0", CultureInfo.InvariantCulture);

    // GetValue<decimal> (not <long>) so a JSON float token like 40000001.0 parses; cast truncates.
    private static int? ReadInt(JsonNode? node) =>
        node is null ? null : (int?)node.GetValue<decimal>();

    private static int ReadRequiredInt(JsonNode? node, string field) =>
        ReadInt(node) ?? throw new HoldedPermanentException(
            $"Holded item is missing required field '{field}' — refusing the page.");

    /// <summary>An absent amount must fail the page, not read as 0.00 — replace semantics would
    /// overwrite the cached line's real amount and still report the sync as a success.</summary>
    private static decimal ReadRequiredDecimalV2(JsonNode? node, string field) =>
        node is null
            ? throw new HoldedPermanentException(
                $"Holded item is missing required field '{field}' — refusing the page.")
            : ReadDecimalV2(node);
}
