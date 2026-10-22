using NodaTime;

namespace Humans.Holded.Contracts;

/// <summary>Both kinds share one payload and one pipeline (create, approve, read); only the endpoint differs.</summary>
public enum HoldedSalesDocumentKind
{
    /// <summary>Full <i>factura</i> — <c>/api/v2/invoices</c>. Requires an identified contact.</summary>
    Invoice,

    /// <summary><i>Factura simplificada</i> — <c>/api/v2/sales-receipts</c>. No counterparty needed.</summary>
    SalesReceipt,
}

/// <summary>
/// <see cref="AccountId"/> is the chart-of-accounts <b>id</b> (24-hex), not the 8-digit number: resolve it through
/// <see cref="IHoldedClient.ListAccountingAccountsAsync"/>.
/// </summary>
public sealed record HoldedSalesDocumentLineInput
{
    public required string Name { get; init; }
    public required decimal Units { get; init; }
    /// <summary>VAT-exclusive.</summary>
    public required decimal Price { get; init; }
    /// <summary>Holded sales tax keys, e.g. <c>s_iva_21</c> / <c>s_iva_0</c>. Empty applies the
    /// contact's default tax, so never leave it empty for a line whose rate matters.</summary>
    public IReadOnlyList<string> Taxes { get; init; } = [];
    /// <summary>Null falls back to Holded's default income account.</summary>
    public string? AccountId { get; init; }
}

public sealed record HoldedSalesDocumentInput
{
    /// <summary>Required for <see cref="HoldedSalesDocumentKind.Invoice"/>; omitted for a receipt.</summary>
    public string? ContactId { get; init; }
    public string? ContactName { get; init; }
    public required Instant Date { get; init; }
    public string? Description { get; init; }
    /// <summary>Internal notes — visible to us, not printed on the document.</summary>
    public string? Notes { get; init; }
    /// <summary>Not printed and, unlike <see cref="Notes"/>, returned by the list endpoints, so a tag is the only
    /// way to find a document by whatever created it.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];
    public required IReadOnlyList<HoldedSalesDocumentLineInput> Lines { get; init; }
}

public sealed record HoldedSalesDocumentDto
{
    public required string Id { get; init; }
    /// <summary>Assigned at approval; empty on a draft.</summary>
    public required string DocNumber { get; init; }
    public required decimal Subtotal { get; init; }
    public required decimal Tax { get; init; }
    public required decimal Total { get; init; }
    public string? Status { get; init; }
    /// <summary>A draft books no revenue and has no number until <see cref="IHoldedClient.ApproveSalesDocumentAsync"/>.
    /// Null when Holded doesn't report the field.</summary>
    public bool? IsDraft { get; init; }
    /// <summary>The verbatim response body, kept for the audited payload on <c>store_invoices</c>.</summary>
    public required string RawJson { get; init; }
}
