using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Humans.Holded.Contracts;
using NodaTime.Text;
using static Humans.Holded.Services.HoldedJson;

namespace Humans.Holded.Services;

internal sealed class HoldedSalesClient(HoldedTransport transport, ILogger<HoldedClient> logger)
{
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

        using var resp = await transport.SendAsync(req, ct);
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
        using var resp = await transport.SendAsync(req, ct);
    }

    public async Task<IReadOnlyList<string>> FindSalesDocumentIdsByTagAsync(
        HoldedSalesDocumentKind kind, string tag, CancellationToken ct = default)
    {
        const int pageSafetyCap = 200; // 40 000 documents — far above a small nonprofit's volume
        var items = await transport.GetPagedAsync($"/api/v2/{SalesSegment(kind)}?limit=200", pageSafetyCap, ct);
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
        using var resp = await transport.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        try
        {
            var node = JsonNode.Parse(body)
                ?? throw new HoldedTransientException("Holded returned empty body");
            WarnIfNotEuro(logger, node, kind.ToString());
            return new HoldedSalesDocumentDto
            {
                Id = ReadRequiredString(Prop(node, "id") ?? JsonValue.Create(documentId), "id"),
                DocNumber = Prop(node, "document_number")?.GetValue<string>() ?? "",
                Subtotal = ReadDecimalV2(Prop(node, "subtotal")),
                Tax = ReadDecimalV2(Prop(node, "tax")),
                Total = ReadRequiredDecimalV2(Prop(node, "total"), "total"),
                IsDraft = Prop(node, "draft")?.GetValue<bool>(),
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
                logger.LogWarning(
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
}
