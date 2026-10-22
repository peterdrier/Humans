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
    private const string UnconfirmedPaymentRefPrefix = "unconfirmed:";

    private const int DefaultRetryAfterSeconds = 5;
    private const int MaxRetryAfterSeconds = 60;

    /// <summary>Holded applies every key a POST/PUT carries, so <c>"code":null</c> would blank a stored tax id.
    /// Omitting nulls means "leave it alone"; nothing here clears a Holded field on purpose.</summary>
    private static readonly JsonSerializerOptions OmitNulls =
        new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    // v2 ledger-entries dates arrive as DD/MM/YYYY and filter on the accounting date; parsed to Madrid midnight.
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
            notes = input.Notes,
            // No tags on the write side: items[].account books the doc to the right department directly.
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
        // A successful create may already be persisted remotely; an unreadable response cannot be retried safely.
        try
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            var node = JsonNode.Parse(body)
                ?? throw new HoldedPermanentException("Holded returned no purchase identity after accepting creation.");
            var id = node["id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(id))
                throw new HoldedPermanentException("Holded returned no purchase identity after accepting creation.");
            return id;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException
            or FormatException or OverflowException)
        {
            throw new HoldedPermanentException(
                "Holded create-purchase response could not be read.", ex);
        }
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
                Notes = Prop(node, "notes")?.GetValue<string>() is { Length: > 0 } notes ? notes : null,
                Tags = ReadTags(Prop(node, "tags")),
                Payments = ReadPaymentsDetail(Prop(node, "payments_detail")),
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException
            or FormatException or OverflowException or UnparsableValueException)
        {
            // A value of an unexpected type belongs to the stored document, so retrying can't help.
            // UnparsableValueException covers ParseApprovedAt on a malformed timestamp.
            throw new HoldedPermanentException(
                $"Holded purchase document {documentId} could not be read.", ex);
        }
    }

    /// <summary>Shape: <c>{"id","amount":"17.00","date":"2026-10-16","bank_id"}</c>. Absent or non-array reads as no
    /// payments. An unreadable entry is dropped rather than failing the document: it could never match in
    /// <see cref="PayPurchaseDocumentAsync"/>, which then returns the <c>unconfirmed:</c> sentinel, as intended.</summary>
    private static IReadOnlyList<HoldedPaymentDetailDto> ReadPaymentsDetail(JsonNode? node)
    {
        if (node is not JsonArray arr) return [];
        var list = new List<HoldedPaymentDetailDto>();
        foreach (var item in arr)
        {
            try
            {
                var id = Prop(item, "id")?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(id)) continue;
                list.Add(new HoldedPaymentDetailDto(
                    id,
                    ReadDecimalV2(Prop(item, "amount")),
                    LocalDatePattern.Iso.Parse(Prop(item, "date")?.GetValue<string>() ?? "").Value,
                    Prop(item, "bank_id")?.GetValue<string>()));
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException
                or FormatException or OverflowException or UnparsableValueException)
            {
                // A GUI-entered payment in an unexpected shape must not fail the document.
            }
        }
        return list;
    }

    public async Task ApprovePurchaseDocumentAsync(string documentId, CancellationToken ct = default)
    {
        // A bodyless POST 411s through Holded's proxy without an explicit Content-Length; empty content sets one.
        using var req = new HttpRequestMessage(
            HttpMethod.Post, $"/api/v2/purchases/{documentId}/approve")
        { Content = new ByteArrayContent([]) };
        AttachAuth(req);
        try
        {
            using var resp = await SendAsync(req, ct);
        }
        // Already approved. A retried push re-approves what an earlier attempt approved; without this the retry
        // writes the whole push off.
        catch (HoldedPermanentException ex) when (ex.StatusCode == 400
            && ex.ResponseBody?.Contains("Document already approved", StringComparison.Ordinal) == true)
        {
            _logger.LogWarning("Holded purchase document {DocumentId} was already approved — treated as success", documentId);
        }
    }

    public async Task DeletePurchaseDocumentAsync(string documentId, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Delete, $"/api/v2/purchases/{documentId}");
        AttachAuth(req);
        using var resp = await SendAsync(req, ct);
    }

    public async Task<string> PayPurchaseDocumentAsync(
        string documentId, decimal amount, string? treasuryId, LocalDate date, string? description,
        CancellationToken ct = default)
    {
        // The POST returns no payment id, so diff payments_detail before and after; a failed before-read throws
        // with nothing posted. Not GET /payments: it stamps a payment dated D as (D-1)T22:00Z.
        var before = (await GetPurchaseDocumentAsync(documentId, ct)).Payments;

        // v2 takes the amount as a decimal *string*; a JSON number is rejected.
        var payload = new
        {
            amount = amount.ToString("F2", CultureInfo.InvariantCulture),
            treasury_id = string.IsNullOrWhiteSpace(treasuryId) ? null : treasuryId,
            date = LocalDatePattern.Iso.Format(date),
            description,
        };

        using var req = new HttpRequestMessage(
            HttpMethod.Post, $"/api/v2/purchases/{documentId}/payments")
        { Content = JsonContent.Create(payload, options: OmitNulls) };
        AttachAuth(req);

        using var resp = await SendAsync(req, ct);

        // From here the payment IS posted: throwing would read as "not paid" and the caller would pay again. The
        // sentinel names the document a human has to check.
        IReadOnlyList<HoldedPaymentDetailDto> after;
        try
        {
            after = (await GetPurchaseDocumentAsync(documentId, ct)).Payments;
        }
        catch (Exception ex) when (ex is HoldedTransientException or HoldedPermanentException)
        {
            _logger.LogWarning(ex,
                "Holded accepted a payment on purchase document {DocumentId} but the document could not be re-read for its id; recording it as unconfirmed.",
                documentId);
            return UnconfirmedPaymentRefPrefix + documentId;
        }

        var known = before.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var matches = after
            .Where(p => !known.Contains(p.Id)
                        && p.Amount == amount
                        && p.Date == date
                        && (string.IsNullOrWhiteSpace(treasuryId)
                            || string.Equals(p.BankId, treasuryId, StringComparison.Ordinal)))
            .ToList();
        if (matches.Count == 1) return matches[0].Id;

        _logger.LogWarning(
            "Holded accepted a payment of {Amount} dated {Date} on purchase document {DocumentId} but payments_detail shows {Count} new matching entries (before: [{Before}], after: [{After}]); recording it as unconfirmed.",
            amount, date, documentId, matches.Count,
            string.Join(", ", before.Select(Describe)), string.Join(", ", after.Select(Describe)));
        return UnconfirmedPaymentRefPrefix + documentId;

        static string Describe(HoldedPaymentDetailDto p) =>
            $"{p.Id} {p.Amount.ToString("F2", CultureInfo.InvariantCulture)} {LocalDatePattern.Iso.Format(p.Date)} {p.BankId}";
    }

    public async Task<string> PostLedgerEntryAsync(
        LocalDate date, int debitAccount, int creditAccount, decimal amount, string description,
        CancellationToken ct = default)
    {
        // Amounts are decimal strings and accounts are ledger numbers, as for payments. The date goes out ISO;
        // DD/MM/YYYY is only how ledger-entries reads come back.
        var money = amount.ToString("F2", CultureInfo.InvariantCulture);
        var payload = new
        {
            date = LocalDatePattern.Iso.Format(date),
            notes = description,
            lines = new[]
            {
                new { account = debitAccount, description, debit = money, credit = "0.00" },
                new { account = creditAccount, description, debit = "0.00", credit = money },
            },
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v2/ledger-entries")
        { Content = JsonContent.Create(payload, options: OmitNulls) };
        AttachAuth(req);

        using var resp = await SendAsync(req, ct);
        string? id = null;
        try
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            id = JsonNode.Parse(body)?["id"]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException
            or FormatException or OverflowException)
        {
            _logger.LogWarning(ex,
                "Holded accepted a ledger entry (debit {Debit}, credit {Credit}, {Amount}) but its response could not be parsed.",
                debitAccount, creditAccount, money);
        }

        if (!string.IsNullOrWhiteSpace(id)) return id;

        // Posted, like the payment case: a throw here would let the caller post it again.
        _logger.LogWarning(
            "Holded accepted a ledger entry (debit {Debit}, credit {Credit}, {Amount}) without a readable id; recording it as unconfirmed.",
            debitAccount, creditAccount, money);
        return UnconfirmedPaymentRefPrefix + "entry";
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
                AccountNum = ReadRequiredInt(Prop(n, "account_num"), "account_num"),
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
        try
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            var node = JsonNode.Parse(body)
                ?? throw new HoldedPermanentException("Holded returned no expense-account identity after accepting creation.");
            var id = node["id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(id))
                throw new HoldedPermanentException("Holded returned no expense-account identity after accepting creation.");
            return id;
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
            // The bad value belongs to the stored document, so retrying can't help, and a raw parse throw would
            // escape the client's typed exceptions. Fails the whole list rather than skipping the doc: these totals
            // become budget actuals, and a short list is wrong money.
            throw new HoldedPermanentException("Holded purchase documents could not be read.", ex);
        }
    }

    public async Task<string> UpsertContactAsync(HoldedContactInput input, CancellationToken ct = default)
    {
        // Create only: v2's contact PUT is a full replacement that resets supplier_record, so a partial PUT mints a
        // second creditor account. custom_id isn't sent (v2 has none). With no address parts bill_address stays
        // null, and OmitNulls drops the key.
        object? billAddress =
            string.IsNullOrWhiteSpace(input.Address) && string.IsNullOrWhiteSpace(input.CountryCode)
                ? null
                : new { address = input.Address, country_code = input.CountryCode };

        var payload = new
        {
            name = input.Name,
            trade_name = input.TradeName,
            type = input.Type,
            iban = input.Iban,
            code = input.TaxCode,
            email = input.Email,
            bill_address = billAddress,
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v2/contacts")
        { Content = JsonContent.Create(payload, options: OmitNulls) };
        AttachAuth(req);

        using var resp = await SendAsync(req, ct);
        try
        {
            var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))
                ?? throw new HoldedPermanentException("Holded returned no contact identity after accepting the upsert.");
            var id = node["id"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(id)) return id;
            throw new HoldedPermanentException("Holded returned no contact identity after accepting the upsert.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException
            or FormatException or OverflowException)
        {
            throw new HoldedPermanentException(
                "Holded contact-upsert response could not be read.", ex);
        }
    }

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
        try
        {
            var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))
                ?? throw new HoldedPermanentException("Holded returned no sales-document identity after accepting creation.");
            var id = Prop(node, "id")?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(id))
                throw new HoldedPermanentException("Holded returned no sales-document identity after accepting creation.");
            return id;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException
            or FormatException or OverflowException)
        {
            throw new HoldedPermanentException(
                "Holded create-sales-document response could not be read.", ex);
        }
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
            // The bad value belongs to the stored document, so a retry can't help.
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
                Total = ReadRequiredDecimalV2(Prop(node, "total"), "total"),
                Status = Prop(node, "status")?.GetValue<string>(),
                IsDraft = Prop(node, "draft")?.GetValue<bool>(),
                RawJson = body,
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException
            or FormatException or OverflowException)
        {
            // The doc is already created and approved, so a retry would duplicate it: permanent.
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
            // The bad value belongs to the stored contact, so retrying can't help. Permanent, so callers that handle
            // the typed exceptions degrade; a raw parse failure would abort ExpenseHoldedPublisher's whole outbox batch.
            throw new HoldedPermanentException(
                $"Holded contact {contactId} could not be read.", ex);
        }
    }

    public async Task DeleteContactAsync(string contactId, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Delete, $"/api/v2/contacts/{contactId}");
        AttachAuth(req);
        try
        {
            using var resp = await SendAsync(req, ct);
        }
        // The goal state already holds: a retried cleanup finds the contact gone.
        catch (HoldedPermanentException ex) when (ex.StatusCode == 404)
        {
            // The id was created seconds ago by this process, so a 404 most likely means the DELETE
            // route is wrong and the contact still exists in Holded.
            _logger.LogError(
                "Holded contact {ContactId} answered 404 to DELETE — it was just created by this process, " +
                "so the route is most likely wrong and the contact still exists in Holded; treated as deleted",
                contactId);
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
            // The page envelope was unreadable; the skip-and-log below covers only one bad item in a good page.
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
                // One bad contact must not cost the others their names: this list is the only source of creditor
                // names on /Finance/Creditors, and its caller turns a throw into all names blank. Detail is logged
                // for the first only, so a bad page can't flood the log.
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

    public async Task<IReadOnlyList<HoldedBankMovementDto>> ListBankMovementsAsync(
        string treasuryAccountId, LocalDate from, LocalDate to, CancellationToken ct = default,
        IReadOnlySet<string>? skipUnreadableDateIds = null)
    {
        const int pageSafetyCap = 50; // far above a small nonprofit's line volume for a 90-day window
        // end_date is exclusive on the live API — send to+1, same rule as ListLedgerEntriesAsync.
        var query =
            $"/api/v2/treasury/accounts/{Uri.EscapeDataString(treasuryAccountId)}/bank-movements" +
            $"?start_date={LocalDatePattern.Iso.Format(from)}" +
            $"&end_date={LocalDatePattern.Iso.Format(to.PlusDays(1))}&limit=200";

        var items = await GetPagedAsync(query, pageSafetyCap, ct);
        try
        {
            var movements = new List<HoldedBankMovementDto>();
            var skipped = 0;
            foreach (var n in items)
            {
                LocalDate date;
                try
                {
                    date = ParseBankMovementDate(n);
                }
                catch (Exception ex) when (ex is HoldedPermanentException or InvalidOperationException
                    or FormatException)
                {
                    // A line with no readable date can't be placed in the window. Skipping is safe only if it can
                    // never be a SEPA candidate: Finance's rival checks count every pending outgoing line, so dropping
                    // one would let an ambiguous booking through. Those fail the page unless the caller named the line
                    // as already surfaced.
                    if (ReadRequiredDecimalV2(Prop(n, "amount"), "amount") < 0m
                        && string.Equals(ReadRequiredString(Prop(n, "status"), "status"), "pending",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        var id = Prop(n, "id")?.GetValue<string>();
                        if (id is null || skipUnreadableDateIds?.Contains(id) != true)
                            throw new HoldedPermanentException(
                                $"Holded bank movement '{id}' in account "
                                + $"{treasuryAccountId} is a pending outgoing line with no readable date.",
                                ex, id);
                        _logger.LogWarning(
                            "Skipping Holded bank movement {MovementId} in account {AccountId}: a pending "
                            + "outgoing line with no readable date, skipped at the caller's request.",
                            id, treasuryAccountId);
                        continue;
                    }
                    if (skipped == 0)
                        _logger.LogWarning("Unreadable Holded bank movement date; skipping the line.");
                    skipped++;
                    continue;
                }

                if (date < from || date > to) continue;

                movements.Add(new HoldedBankMovementDto
                {
                    Id = ReadRequiredString(Prop(n, "id"), "id"),
                    Date = date,
                    Amount = ReadRequiredDecimalV2(Prop(n, "amount"), "amount"),
                    Description = Prop(n, "description")?.GetValue<string>(),
                    // Never defaulted: "pending" is the one status the SEPA sweep reads as bookable, so inventing it
                    // could turn a settled line bookable. Any other present value already fails closed.
                    Status = ReadRequiredString(Prop(n, "status"), "status").ToLowerInvariant(),
                    Origin = Prop(n, "origin")?.GetValue<string>(),
                });
            }

            if (skipped > 0)
                _logger.LogWarning(
                    "Skipped {Skipped} Holded bank movement(s) with an unreadable date in account {AccountId}.",
                    skipped, treasuryAccountId);
            return movements;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException
            or FormatException or OverflowException or UnparsableValueException)
        {
            // A manufactured line would book against a movement that doesn't exist, so an unreadable
            // id/amount/status fails the page.
            throw new HoldedPermanentException(
                $"Holded bank-movements {from}..{to} for account {treasuryAccountId} could not be read.",
                ex);
        }
    }

    public async Task ReconcileBankMovementAsync(
        string treasuryAccountId, string movementId,
        IReadOnlyList<HoldedReconcileDocumentRef> documents, CancellationToken ct = default)
    {
        var payload = new
        {
            documents = documents.Select(d => new
            {
                document_id = d.DocumentId,
                document_type = d.DocumentType,
            }),
        };

        using var req = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v2/treasury/accounts/{Uri.EscapeDataString(treasuryAccountId)}" +
            $"/bank-movements/{Uri.EscapeDataString(movementId)}/reconcile")
        { Content = JsonContent.Create(payload, options: OmitNulls) };
        AttachAuth(req);

        using var resp = await SendAsync(req, ct);
    }

    /// <summary>A bank line has no <c>date</c> key, only <c>booking_date</c> and <c>value_date</c> (ISO 8601 with an
    /// offset). Booking is what Holded stamps on the reconciled payment and entry, so it is the date; value is the
    /// fallback. The date part is taken as given, never shifted to Madrid: a midnight-UTC Sabadell line would move
    /// a day. Throws <c>HoldedPermanentException</c> when neither parses.</summary>
    private static LocalDate ParseBankMovementDate(JsonNode line)
    {
        var s = Prop(line, "booking_date")?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(s)) s = Prop(line, "value_date")?.GetValue<string>() ?? "";

        var parsed = OffsetDateTimePattern.ExtendedIso.Parse(s);
        if (parsed.Success) return parsed.Value.Date;

        throw new HoldedPermanentException($"Holded bank movement date '{s}' could not be parsed.");
    }

    /// <summary>Holded sends an absent sub-record as an empty array, and the string indexer throws on anything but an
    /// object, so every nested read goes through <see cref="Prop"/>.</summary>
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

    /// <summary>Null when the address is absent or empty.</summary>
    private static string? FormatAddress(JsonNode? billAddress)
    {
        var parts = new[] { "address", "postal_code", "city", "province", "country" }
            .Select(k => Prop(billAddress, k)?.GetValue<string>())
            .Where(v => !string.IsNullOrWhiteSpace(v));
        var joined = string.Join(", ", parts);
        return joined.Length == 0 ? null : joined;
    }

    /// <summary>Null when <paramref name="node"/> is not an object. The raw indexer throws there, which on a list
    /// read costs the whole page rather than one field.</summary>
    private static JsonNode? Prop(JsonNode? node, string name) =>
        node is JsonObject obj ? obj[name] : null;

    /// <summary>Empty when absent or not an array: <see cref="JsonNode.AsArray"/> throws on a non-array, and Holded
    /// will send an absent collection as a scalar.</summary>
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
                // A manufactured 0 key would survive into ReplaceLedgerWindowAsync and evict the real row.
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
            // Fails the whole page rather than skipping the line: balances are summed from these, so a dropped
            // line reads as a settled entry that never happened.
            throw new HoldedPermanentException(
                $"Holded ledger-entries {from}..{to} could not be read.", ex);
        }
    }

    public async Task<IReadOnlyList<HoldedAccountDto>> ListAccountingAccountsAsync(
        CancellationToken ct = default)
    {
        const int pageSafetyCap = 5; // a few hundred accounts, unpaginated in practice — headroom
        var items = await GetPagedAsync("/api/v2/accounting-accounts?limit=200", pageSafetyCap, ct);
        try
        {
            return items.Select(n => new HoldedAccountDto
            {
                Id = ReadRequiredString(Prop(n, "id"), "id"),
                // Required: the number keys the mirror, picks the PGC group and drives the POV flip. A manufactured 0
                // would become an "Unclassified" account with a sign-flipped balance, reconciled every night.
                Number = ReadRequiredInt(Prop(n, "number"), "number"),
                Name = Prop(n, "name")?.GetValue<string>() ?? "",
                Group = Prop(n, "group")?.GetValue<string>(),
                Debit = ReadRequiredDecimalV2(Prop(n, "debit"), "debit"),
                Credit = ReadRequiredDecimalV2(Prop(n, "credit"), "credit"),
                Balance = ReadRequiredDecimalV2(Prop(n, "balance"), "balance"),
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

    /// <summary>Throws at pageSafetyCap rather than returning a truncated list: these feed replace-semantics
    /// reconciliation, so a short fetch would delete rows that still exist in Holded.</summary>
    private async Task<List<JsonNode>> GetPagedAsync(
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
                AttachAuth(req);
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

    private static Instant ParseLedgerDate(string s) =>
        DateFormattingExtensions.HoldedLedgerDatePattern.Parse(s).Value
            .AtStartOfDayInZone(MadridZone).ToInstant();

    private static Instant ParseIsoDate(string s) =>
        LocalDatePattern.Iso.Parse(s).Value.AtStartOfDayInZone(MadridZone).ToInstant();

    /// <summary>Offset-less ISO (e.g. "2024-01-15T13:00:00"), read as Madrid local time like every other Holded
    /// date-time.</summary>
    private static Instant? ParseApprovedAt(string? s) =>
        string.IsNullOrEmpty(s)
            ? null
            : LocalDateTimePattern.GeneralIso.Parse(s).Value.InZoneLeniently(MadridZone).ToInstant();

    private static HoldedPurchaseDocListItemDto ParsePurchaseDoc(JsonNode? n) => new()
    {
        Id = ReadRequiredString(Prop(n, "id"), "id"),
        DocNumber = Prop(n, "document_number")?.GetValue<string>() ?? "",
        ContactId = Prop(n, "contact_id")?.GetValue<string>(),
        ContactName = Prop(n, "contact_name")?.GetValue<string>() ?? "",
        Description = Prop(n, "description")?.GetValue<string>(),
        Notes = Prop(n, "notes")?.GetValue<string>() is { Length: > 0 } notes ? notes : null,
        Date = ParseIsoDate(Prop(n, "date")?.GetValue<string>() ?? ""),
        Subtotal = ReadDecimalV2(Prop(n, "subtotal")),
        Tax = ReadDecimalV2(Prop(n, "tax")),
        // Total feeds the budget actuals; an absent field must fail the page, not upsert 0.00.
        Total = ReadRequiredDecimalV2(Prop(n, "total"), "total"),
        PaymentsPending = ReadDecimalV2(Prop(n, "payments_pending")),
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
            // upsert can echo the IBAN back: mask it here. ResponseBody keeps the raw body.
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

    // v2 sends decimals as strings (e.g. "121.00").
    private static decimal ReadDecimalV2(JsonNode? node) =>
        decimal.Parse(node?.GetValue<string>() ?? "0", CultureInfo.InvariantCulture);

    // Holded may encode an integer as a JSON float (40000001.0). Accept that shape,
    // but never truncate a fractional identifier onto a different account or ledger row.
    private static int? ReadInt(JsonNode? node)
    {
        if (node is null) return null;
        var value = node.GetValue<decimal>();
        if (value != decimal.Truncate(value))
            throw new FormatException("Holded integer field contains a fractional value.");
        return (int)value;
    }

    private static int ReadRequiredInt(JsonNode? node, string field) =>
        ReadInt(node) ?? throw new HoldedPermanentException(
            $"Holded item is missing required field '{field}' — refusing the page.");

    /// <summary>Null, absent and blank all fail the page: Holded has been seen sending <c>""</c> for a field it
    /// usually fills.</summary>
    private static string ReadRequiredString(JsonNode? node, string field) =>
        node?.GetValue<string>() is { Length: > 0 } s && !string.IsNullOrWhiteSpace(s)
            ? s
            : throw new HoldedPermanentException(
                $"Holded item is missing required field '{field}' — refusing the page.");

    /// <summary>An absent amount must fail the page, not read as 0.00: replace semantics would overwrite the real
    /// amount and still report success.</summary>
    private static decimal ReadRequiredDecimalV2(JsonNode? node, string field) =>
        node is null
            ? throw new HoldedPermanentException(
                $"Holded item is missing required field '{field}' — refusing the page.")
            : ReadDecimalV2(node);
}
