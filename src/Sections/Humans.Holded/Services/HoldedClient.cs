using Humans.Base.Interfaces;
using Humans.Holded.Contracts;
using Microsoft.Extensions.Options;
using NodaTime;
using Humans.Base.Extensions;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using NodaTime.Text;
using static Humans.Holded.Services.HoldedJson;
using System.Net.Http.Headers;

namespace Humans.Holded.Services;

internal sealed class HoldedClient : IHoldedClient
{
    private readonly HoldedTransport _transport;
    private readonly ILogger<HoldedClient> _logger;
    private readonly IOrgTimeZone _orgTimeZone;

    /// <summary>Holded's business dates carry no offset; they are read and written in the org zone.</summary>
    private DateTimeZone Zone => _orgTimeZone.Zone;

    public HoldedClient(
        HttpClient http,
        IOptions<HoldedClientOptions> options,
        ILogger<HoldedClient> logger,
        IHoldedCallLog callLog,
        IClock clock,
        IOrgTimeZone orgTimeZone)
    {
        _orgTimeZone = orgTimeZone;
        _transport = new HoldedTransport(http, options, logger, callLog, clock);
        _logger = logger;
    }

    public bool IsConfigured => _transport.IsConfigured;

    public Task<string> CreateTreasuryPaymentAsync(
        HoldedTreasuryPaymentKind kind, string contactId, string treasuryId, decimal amount, LocalDate date,
        string? description, CancellationToken ct = default) =>
        CreatePaymentAsync(kind, contactId, treasuryId, amount, date, description, ct);

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

        using var resp = await _transport.SendAsync(req, ct);
        string? id = null;
        try
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            id = JsonNode.Parse(body)?["id"]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException
            or FormatException or OverflowException)
        {
            _logger.LogWarning(
                "Holded accepted a ledger entry (debit {Debit}, credit {Credit}, {Amount}) but its response could not be parsed: {Reason}",
                debitAccount, creditAccount, money, ex.Message);
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
            var items = await _transport.GetPagedAsync("/api/v2/expenses-accounts?limit=200", pageSafetyCap, ct);
            return items.Select(n => new HoldedExpenseAccountDto
            {
                Id = ReadRequiredString(Prop(n, "id"), "id"),
                AccountNum = ReadRequiredInt(Prop(n, "account_num"), "account_num"),
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
        using var resp = await _transport.SendAsync(req, ct);
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

        var items = await _transport.GetPagedAsync(query, pageSafetyCap, ct);
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
                EntryId = Prop(n, "id")?.GetValue<string>(),
                Checked = Prop(n, "checked")?.GetValue<bool>() ?? false,
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
        var items = await _transport.GetPagedAsync("/api/v2/accounting-accounts?limit=200", pageSafetyCap, ct);
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
        using var resp = await _transport.SendAsync(req, ct);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        try
        {
            // Parse inside the try: a truncated 200 body must surface as a typed Holded
            // exception so /Holded degrades to "unreachable" instead of a raw 500.
            var node = await JsonNode.ParseAsync(stream, cancellationToken: ct)
                ?? throw new HoldedTransientException("Holded returned empty body");
            return new HoldedUsageDto
            {
                Period = ReadRequiredString(Prop(node, "period"), "period"),
                Usage = Prop(node, "usage")?.GetValue<long>()
                    ?? throw new HoldedPermanentException("Holded usage is missing required field 'usage'."),
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException
            or FormatException or OverflowException)
        {
            throw new HoldedPermanentException("Holded usage could not be read.", ex);
        }
    }

    private Instant ParseLedgerDate(string s) =>
        DateFormattingExtensions.HoldedLedgerDatePattern.Parse(s).Value
            .AtStartOfDayInZone(Zone).ToInstant();

    public async Task<string> CreateContactAsync(HoldedContactInput input, CancellationToken ct = default)
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

        using var resp = await _transport.SendAsync(req, ct);
        try
        {
            var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))
                ?? throw new HoldedPermanentException("Holded returned no contact identity after accepting creation.");
            var id = node["id"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(id)) return id;
            throw new HoldedPermanentException("Holded returned no contact identity after accepting creation.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException
            or FormatException or OverflowException)
        {
            throw new HoldedPermanentException(
                "Holded create-contact response could not be read.", ex);
        }
    }

    public async Task<HoldedContactDto> GetContactAsync(string contactId, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v2/contacts/{contactId}");
        using var resp = await _transport.SendAsync(req, ct);
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
            // the typed exceptions degrade; a raw parse failure would abort ExpenseReportBackgroundProcessor's whole outbox batch.
            throw new HoldedPermanentException(
                $"Holded contact {contactId} could not be read.", ex);
        }
    }

    public async Task DeleteContactAsync(string contactId, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Delete, $"/api/v2/contacts/{contactId}");
        try
        {
            using var resp = await _transport.SendAsync(req, ct);
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
            items = await _transport.GetPagedAsync("/api/v2/contacts?limit=200", pageSafetyCap, ct);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException
            or FormatException or OverflowException)
        {
            throw new HoldedPermanentException("Holded contacts could not be read.", ex);
        }

        var contacts = new List<HoldedContactDto>();
        foreach (var n in items)
        {
            try
            {
                contacts.Add(ParseContact(n));
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException
                or FormatException or OverflowException or HoldedPermanentException)
            {
                throw new HoldedPermanentException("Holded contacts contain an unreadable contact.", ex);
            }
        }

        return contacts;
    }

    /// <summary>Holded sends an absent sub-record as an empty array, and the string indexer throws on anything but an
    /// object, so every nested read goes through <see cref="Prop"/>.</summary>
    private static HoldedContactDto ParseContact(JsonNode node, string? fallbackId = null) => new()
    {
        Id = ReadRequiredString(Prop(node, "id") ?? JsonValue.Create(fallbackId), "id"),
        Name = Prop(node, "name")?.GetValue<string>(),
        SupplierAccountNum = ReadInt(Prop(Prop(node, "supplier_record"), "num")),
        ClientAccountNum = ReadInt(Prop(Prop(node, "client_record"), "num")),
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

    public async Task<string> CreatePurchaseDocumentAsync(
        HoldedPurchaseDocumentInput input, CancellationToken ct = default)
    {
        var payload = new
        {
            contact_id = input.ContactId,
            contact_name = input.ContactName,
            date = LocalDatePattern.Iso.Format(input.Date.InZone(Zone).Date),
            description = input.Description,
            number = input.Number,
            notes = input.Notes,
            // No tags on the write side: items[].account books the doc to the right department directly.
            items = input.Lines.Select(l => new
            {
                name = l.Description,
                units = 1,
                price = l.Amount,
                account = l.AccountId,
                taxes = l.Taxes,
            })
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v2/purchases")
        { Content = JsonContent.Create(payload, options: OmitNulls) };

        using var resp = await _transport.SendAsync(req, ct);
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

        using var resp = await _transport.SendAsync(req, ct);
    }

    public async Task<HoldedPurchaseDocumentDto> GetPurchaseDocumentAsync(
        string documentId, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v2/purchases/{documentId}");

        using var resp = await _transport.SendAsync(req, ct);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        try
        {
            var node = await JsonNode.ParseAsync(stream, cancellationToken: ct)
                ?? throw new HoldedTransientException("Holded returned empty body");
            WarnIfNotEuro(_logger, node, "purchase");

            return new HoldedPurchaseDocumentDto
            {
                ApprovedAt = ParseApprovedAt(Prop(node, "approved_at")?.GetValue<string>()),
                Notes = Prop(node, "notes")?.GetValue<string>() is { Length: > 0 } notes ? notes : null,
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

    public Task ApprovePurchaseDocumentAsync(string documentId, CancellationToken ct = default) =>
        ApproveDocumentAsync("purchases", documentId, ct);

    private async Task ApproveDocumentAsync(string segment, string documentId, CancellationToken ct)
    {
        // A bodyless POST 411s through Holded's proxy without an explicit Content-Length; empty content sets one.
        using var req = new HttpRequestMessage(
            HttpMethod.Post, $"/api/v2/{segment}/{documentId}/approve")
        { Content = new ByteArrayContent([]) };
        try
        {
            using var resp = await _transport.SendAsync(req, ct);
        }
        // Already approved. A retried push re-approves what an earlier attempt approved; without this the retry
        // writes the whole push off.
        catch (HoldedPermanentException ex) when (ex.StatusCode == 400
            && ex.ResponseBody?.Contains("Document already approved", StringComparison.Ordinal) == true)
        {
            _logger.LogWarning("Holded {Segment} document {DocumentId} was already approved — treated as success",
                segment, documentId);
        }
    }

    public async Task DeletePurchaseDocumentAsync(string documentId, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Delete, $"/api/v2/purchases/{documentId}");
        using var resp = await _transport.SendAsync(req, ct);
    }

    public Task<string> PayPurchaseDocumentAsync(
        string documentId, decimal amount, string? treasuryId, LocalDate date, string? description,
        CancellationToken ct = default) =>
        HoldedDocumentPayment.PayAsync(
            _transport, _logger, "purchases", documentId,
            async c => (await GetPurchaseDocumentAsync(documentId, c)).Payments,
            amount, treasuryId, date, description, ct);

    public async Task<IReadOnlyList<HoldedPurchaseDocListItemDto>> ListPurchaseDocumentsAsync(
        CancellationToken ct = default)
    {
        const int pageSafetyCap = 200; // 40 000 docs — far above a small nonprofit's volume
        var items = await _transport.GetPagedAsync("/api/v2/purchases?limit=200", pageSafetyCap, ct);
        try
        {
            return items.Select(n =>
            {
                WarnIfNotEuro(_logger, n, "purchase");
                return ParsePurchaseDoc(n);
            }).ToList();
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

    private Instant ParseIsoDate(string s) =>
        LocalDatePattern.Iso.Parse(s).Value.AtStartOfDayInZone(Zone).ToInstant();

    /// <summary>Offset-less ISO (e.g. "2024-01-15T13:00:00"), read as org-zone local time like every other Holded
    /// date-time.</summary>
    private Instant? ParseApprovedAt(string? s) =>
        string.IsNullOrEmpty(s)
            ? null
            : LocalDateTimePattern.GeneralIso.Parse(s).Value.InZoneLeniently(Zone).ToInstant();

    private HoldedPurchaseDocListItemDto ParsePurchaseDoc(JsonNode? n) => new()
    {
        Id = ReadRequiredString(Prop(n, "id"), "id"),
        DocNumber = Prop(n, "document_number")?.GetValue<string>() ?? "",
        ContactId = Prop(n, "contact_id")?.GetValue<string>(),
        ContactName = Prop(n, "contact_name")?.GetValue<string>() ?? "",
        Description = Prop(n, "description")?.GetValue<string>(),
        Date = ParseIsoDate(Prop(n, "date")?.GetValue<string>() ?? ""),
        Subtotal = ReadDecimalV2(Prop(n, "subtotal")),
        Tax = ReadDecimalV2(Prop(n, "tax")),
        // Total feeds the budget actuals; an absent field must fail the page, not upsert 0.00.
        Total = ReadRequiredDecimalV2(Prop(n, "total"), "total"),
        // Approved documents must not disappear from the live open-document list as fabricated paid balances.
        // Drafts can omit this field; absent stays unknown rather than reading as fully paid.
        PaymentsPending = Prop(n, "draft")?.GetValue<bool>() == false
            ? ReadRequiredDecimalV2(Prop(n, "payments_pending"), "payments_pending")
            : Prop(n, "payments_pending") is { } pending ? ReadDecimalV2(pending) : null,
        IsDraft = Prop(n, "draft")?.GetValue<bool>(),
        Currency = Prop(n, "currency")?.GetValue<string>() ?? "eur",
        Tags = ReadTags(Prop(n, "tags")),
        Lines = Arr(Prop(n, "lines")).Select(p => new HoldedPurchaseLineDto
        {
            AccountId = Prop(p, "account")?.GetValue<string>(),
            Tags = ReadTags(Prop(p, "tags")),
        }).ToList(),
    };

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

        using var resp = await _transport.SendAsync(req, ct);
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

    public Task ApproveSalesDocumentAsync(
        HoldedSalesDocumentKind kind, string documentId, CancellationToken ct = default) =>
        ApproveDocumentAsync(SalesSegment(kind), documentId, ct);

    public async Task<IReadOnlyList<string>> FindSalesDocumentIdsByTagAsync(
        HoldedSalesDocumentKind kind, string tag, CancellationToken ct = default)
    {
        const int pageSafetyCap = 200; // 40 000 documents — far above a small nonprofit's volume
        var items = await _transport.GetPagedAsync($"/api/v2/{SalesSegment(kind)}?limit=200", pageSafetyCap, ct);
        try
        {
            return items
                .Where(n => ReadTags(Prop(n, "tags")).Contains(tag, StringComparer.Ordinal))
                // A match without an id cannot be dropped: the caller reads an empty result as
                // "nothing issued yet" and creates a second approved document.
                .Select(n => ReadRequiredString(Prop(n, "id"), "id"))
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
        using var resp = await _transport.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        try
        {
            var node = JsonNode.Parse(body)
                ?? throw new HoldedTransientException("Holded returned empty body");
            WarnIfNotEuro(_logger, node, kind.ToString());
            return new HoldedSalesDocumentDto
            {
                Id = ReadRequiredString(Prop(node, "id") ?? JsonValue.Create(documentId), "id"),
                DocNumber = Prop(node, "document_number")?.GetValue<string>() ?? "",
                Subtotal = ReadDecimalV2(Prop(node, "subtotal")),
                Tax = ReadDecimalV2(Prop(node, "tax")),
                Total = ReadRequiredDecimalV2(Prop(node, "total"), "total"),
                IsDraft = Prop(node, "draft")?.GetValue<bool>(),
                ContactId = Prop(node, "contact_id")?.GetValue<string>() is { Length: > 0 } contactId ? contactId : null,
                PaymentsPending = Prop(node, "payments_pending") is { } pending ? ReadDecimalV2(pending) : null,
                Payments = ReadPaymentsDetail(Prop(node, "payments_detail")),
                Lines = ReadLines(node, kind, documentId),
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

    /// <summary>The document's PDF as Holded renders it. v2 answers either the file itself or, like v1, JSON whose
    /// <c>data</c> is the file in base64; both are accepted.</summary>
    public async Task<byte[]> GetSalesDocumentPdfAsync(
        HoldedSalesDocumentKind kind, string documentId, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v2/{SalesSegment(kind)}/{documentId}/pdf");
        using var resp = await _transport.SendAsync(req, ct);
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        if (bytes.AsSpan().StartsWith("%PDF"u8)) return bytes;
        try
        {
            var data = Prop(JsonNode.Parse(bytes), "data")?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(data)) return Convert.FromBase64String(data);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            throw new HoldedPermanentException($"Holded {SalesSegment(kind)} document {documentId} PDF could not be read.", ex);
        }
        throw new HoldedPermanentException($"Holded returned no PDF for {SalesSegment(kind)} document {documentId}.");
    }

    /// <summary>Holded emails the document's PDF to <paramref name="emails"/> from its own mailer.</summary>
    public async Task SendSalesDocumentAsync(
        HoldedSalesDocumentKind kind, string documentId, IReadOnlyList<string>? emails, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v2/{SalesSegment(kind)}/{documentId}/send")
        { Content = JsonContent.Create(new { emails }, options: OmitNulls) };
        using var resp = await _transport.SendAsync(req, ct);
    }

    /// <summary>Holded takes only payments above zero on a sales document: money back is a separate payment.</summary>
    public Task<string> PaySalesDocumentAsync(
        HoldedSalesDocumentKind kind, string documentId, decimal amount, string treasuryId, LocalDate date,
        string? description, CancellationToken ct = default) =>
        HoldedDocumentPayment.PayAsync(
            _transport, _logger, SalesSegment(kind), documentId,
            async c => (await GetSalesDocumentAsync(kind, documentId, c)).Payments,
            amount, treasuryId, date, description, ct);

    /// <summary>v2's line <c>tax</c> is the line's primary tax percent (the document-level <c>tax</c> is an amount).
    /// This read runs right after an approval, so a line it cannot read never fails it, which would strand an approved
    /// factura: the document reads as having no lines and is logged, and Store refuses to adopt it.</summary>
    private IReadOnlyList<HoldedSalesDocumentLineDto> ReadLines(
        JsonNode node, HoldedSalesDocumentKind kind, string documentId)
    {
        var lines = new List<HoldedSalesDocumentLineDto>();
        foreach (var l in Arr(Prop(node, "lines")))
        {
            if (ReadOptionalDecimal(Prop(l, "units")) is not { } units
                || ReadOptionalDecimal(Prop(l, "price")) is not { } price
                || ReadOptionalDecimal(Prop(l, "tax")) is not { } taxPercent)
            {
                _logger.LogWarning(
                    "Holded {Kind} document {DocumentId} has a line without a readable units, price or tax; " +
                    "its lines read as none", kind, documentId);
                return [];
            }
            var name = Prop(l, "name") is JsonValue v && v.TryGetValue<string>(out var n) ? n : "";
            lines.Add(new HoldedSalesDocumentLineDto(name, units, price, taxPercent));
        }
        return lines;
    }

    private static decimal? ReadOptionalDecimal(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s)
            && decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var d)
            ? d
            : null;

    private object BuildSalesDocumentPayload(HoldedSalesDocumentInput input) => new
    {
        contact_id = input.ContactId,
        contact_name = input.ContactName,
        date = LocalDatePattern.Iso.Format(input.Date.InZone(Zone).Date),
        description = input.Description,
        notes = input.Notes,
        tags = input.Tags,
        currency = "EUR",
        discount = input.DiscountPercent,
        items = input.Lines.Select(l => new
        {
            name = l.Name,
            units = l.Units,
            price = l.Price,
            taxes = l.Taxes,
            account = l.AccountId,
        }).ToList(),
    };

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

        var items = await _transport.GetPagedAsync(query, pageSafetyCap, ct);
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
                    BookedAt = ParseBookedAt(n),
                    Amount = ReadRequiredDecimalV2(Prop(n, "amount"), "amount"),
                    Description = Prop(n, "description")?.GetValue<string>(),
                    // Never defaulted: "pending" is the one status the SEPA sweep reads as bookable, so inventing it
                    // could turn a settled line bookable. Any other present value already fails closed.
                    Status = ReadRequiredString(Prop(n, "status"), "status").ToLowerInvariant(),
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

        using var resp = await _transport.SendAsync(req, ct);
    }

    /// <summary><c>POST /payments</c>: money in (<c>collection</c>) or out (<c>payment</c>) on a contact, outside any
    /// document. Returns the payment's id, or <c>unconfirmed:payment</c> when Holded accepted it without a readable
    /// one; it is posted either way.</summary>
    public async Task<string> CreatePaymentAsync(
        HoldedTreasuryPaymentKind kind, string contactId, string treasuryId, decimal amount, LocalDate date,
        string? description, CancellationToken ct = default)
    {
        var payload = new
        {
            type = kind == HoldedTreasuryPaymentKind.Collection ? "collection" : "payment",
            contact_id = contactId,
            bank_account_id = treasuryId,
            // v2 takes amounts as decimal strings, as on document payments.
            amount = amount.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
            date = LocalDatePattern.Iso.Format(date),
            description,
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v2/payments")
        { Content = JsonContent.Create(payload, options: OmitNulls) };
        using var resp = await _transport.SendAsync(req, ct);
        try
        {
            var id = Prop(JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct)), "id")?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(id)) return id;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            _logger.LogWarning("Holded accepted a {Kind} of {Amount} on contact {ContactId} but its id could not be read: {Error}",
                kind, amount, contactId, ex.Message);
        }
        return UnconfirmedPaymentRefPrefix + "payment";
    }

    private static Instant? ParseBookedAt(JsonNode line)
    {
        var parsed = OffsetDateTimePattern.ExtendedIso.Parse(Prop(line, "booking_date")?.GetValue<string>() ?? "");
        return parsed.Success ? parsed.Value.ToInstant() : null;
    }

    /// <summary>A bank line has no <c>date</c> key, only <c>booking_date</c> and <c>value_date</c> (ISO 8601 with an
    /// offset). Booking is what Holded stamps on the reconciled payment and entry, so it is the date; value is the
    /// fallback. The date part is taken as given, never shifted to the org zone: a midnight-UTC Sabadell line would move
    /// a day. Throws <c>HoldedPermanentException</c> when neither parses.</summary>

    private static LocalDate ParseBankMovementDate(JsonNode line)
    {
        var s = Prop(line, "booking_date")?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(s)) s = Prop(line, "value_date")?.GetValue<string>() ?? "";

        var parsed = OffsetDateTimePattern.ExtendedIso.Parse(s);
        if (parsed.Success) return parsed.Value.Date;

        throw new HoldedPermanentException($"Holded bank movement date '{s}' could not be parsed.");
    }
}
