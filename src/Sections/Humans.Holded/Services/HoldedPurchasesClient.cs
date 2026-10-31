using System.Net.Http.Headers;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Humans.Holded.Contracts;
using NodaTime;
using NodaTime.Text;
using static Humans.Holded.Services.HoldedJson;

namespace Humans.Holded.Services;

internal sealed class HoldedPurchasesClient(HoldedTransport transport, ILogger<HoldedClient> logger)
{
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

        using var resp = await transport.SendAsync(req, ct);
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

        using var resp = await transport.SendAsync(req, ct);
    }

    public async Task<HoldedPurchaseDocumentDto> GetPurchaseDocumentAsync(
        string documentId, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/v2/purchases/{documentId}");

        using var resp = await transport.SendAsync(req, ct);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        try
        {
            var node = await JsonNode.ParseAsync(stream, cancellationToken: ct)
                ?? throw new HoldedTransientException("Holded returned empty body");

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
        try
        {
            using var resp = await transport.SendAsync(req, ct);
        }
        // Already approved. A retried push re-approves what an earlier attempt approved; without this the retry
        // writes the whole push off.
        catch (HoldedPermanentException ex) when (ex.StatusCode == 400
            && ex.ResponseBody?.Contains("Document already approved", StringComparison.Ordinal) == true)
        {
            logger.LogWarning("Holded purchase document {DocumentId} was already approved — treated as success", documentId);
        }
    }

    public async Task DeletePurchaseDocumentAsync(string documentId, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Delete, $"/api/v2/purchases/{documentId}");
        using var resp = await transport.SendAsync(req, ct);
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

        using var resp = await transport.SendAsync(req, ct);

        // From here the payment IS posted: throwing would read as "not paid" and the caller would pay again. The
        // sentinel names the document a human has to check.
        IReadOnlyList<HoldedPaymentDetailDto> after;
        try
        {
            after = (await GetPurchaseDocumentAsync(documentId, ct)).Payments;
        }
        catch (Exception ex) when (ex is HoldedTransientException or HoldedPermanentException)
        {
            logger.LogWarning(ex,
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

        logger.LogWarning(
            "Holded accepted a payment of {Amount} dated {Date} on purchase document {DocumentId} but payments_detail shows {Count} new matching entries (before: [{Before}], after: [{After}]); recording it as unconfirmed.",
            amount, date, documentId, matches.Count,
            string.Join(", ", before.Select(Describe)), string.Join(", ", after.Select(Describe)));
        return UnconfirmedPaymentRefPrefix + documentId;

        static string Describe(HoldedPaymentDetailDto p) =>
            $"{p.Id} {p.Amount.ToString("F2", CultureInfo.InvariantCulture)} {LocalDatePattern.Iso.Format(p.Date)} {p.BankId}";
    }

    public async Task<IReadOnlyList<HoldedPurchaseDocListItemDto>> ListPurchaseDocumentsAsync(
        CancellationToken ct = default)
    {
        const int pageSafetyCap = 200; // 40 000 docs — far above a small nonprofit's volume
        var items = await transport.GetPagedAsync("/api/v2/purchases?limit=200", pageSafetyCap, ct);
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
        Date = ParseIsoDate(Prop(n, "date")?.GetValue<string>() ?? ""),
        Subtotal = ReadDecimalV2(Prop(n, "subtotal")),
        Tax = ReadDecimalV2(Prop(n, "tax")),
        // Total feeds the budget actuals; an absent field must fail the page, not upsert 0.00.
        Total = ReadRequiredDecimalV2(Prop(n, "total"), "total"),
        // Approved documents must not disappear from the live open-document list as fabricated paid balances.
        // Drafts can omit this field and are excluded from booking independently by their draft flag.
        PaymentsPending = Prop(n, "draft")?.GetValue<bool>() == false
            ? ReadRequiredDecimalV2(Prop(n, "payments_pending"), "payments_pending")
            : ReadDecimalV2(Prop(n, "payments_pending")),
        IsDraft = Prop(n, "draft")?.GetValue<bool>(),
        Currency = ReadEuroCurrency(Prop(n, "currency")),
        Tags = ReadTags(Prop(n, "tags")),
        Lines = Arr(Prop(n, "lines")).Select(p => new HoldedPurchaseLineDto
        {
            AccountId = Prop(p, "account")?.GetValue<string>(),
            Tags = ReadTags(Prop(p, "tags")),
        }).ToList(),
    };
}
