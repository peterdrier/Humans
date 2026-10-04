using NodaTime;

namespace Humans.Holded.Contracts;

/// <summary>
/// Which outbound sales document Holded should create. The two share one payload shape and
/// one pipeline (create → approve → read); only the endpoint segment differs.
/// </summary>
public enum HoldedSalesDocumentKind
{
    /// <summary>Full <i>factura</i> — <c>/api/v2/invoices</c>. Requires an identified contact.</summary>
    Invoice,

    /// <summary><i>Factura simplificada</i> — <c>/api/v2/sales-receipts</c>. No counterparty needed.</summary>
    SalesReceipt,
}

/// <summary>
/// One line of an outbound sales document. <see cref="AccountId"/> is the Holded chart-of-accounts
/// <b>id</b> (24-hex), not the 8-digit number — resolve it through
/// <see cref="IHoldedClient.ListAccountingAccountsAsync"/> before building the line.
/// </summary>
public sealed record HoldedSalesDocumentLineInput
{
    public required string Name { get; init; }
    public required decimal Units { get; init; }
    /// <summary>Unit price, VAT-exclusive.</summary>
    public required decimal Price { get; init; }
    /// <summary>Holded sales tax keys, e.g. <c>s_iva_21</c> / <c>s_iva_0</c>. Empty applies the
    /// contact's default tax, so never leave it empty for a line whose rate matters.</summary>
    public IReadOnlyList<string> Taxes { get; init; } = [];
    /// <summary>Holded account id this line books to. Null falls back to Holded's default income account.</summary>
    public string? AccountId { get; init; }
}

/// <summary>Create payload for an invoice or sales receipt.</summary>
public sealed record HoldedSalesDocumentInput
{
    /// <summary>Required for <see cref="HoldedSalesDocumentKind.Invoice"/>; omitted for a receipt.</summary>
    public string? ContactId { get; init; }
    public string? ContactName { get; init; }
    public required Instant Date { get; init; }
    public string? Description { get; init; }
    /// <summary>Internal notes — visible to us, not printed on the document.</summary>
    public string? Notes { get; init; }
    /// <summary>Internal labels. Not printed, and — unlike <see cref="Notes"/> — returned by the
    /// list endpoints, so a tag is the only thing that makes a document findable by whatever
    /// created it. See <see cref="IHoldedClient.FindSalesDocumentIdsByTagAsync"/>.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];
    public required IReadOnlyList<HoldedSalesDocumentLineInput> Lines { get; init; }
}

/// <summary>A sales document as Holded returns it after approval.</summary>
public sealed record HoldedSalesDocumentDto
{
    public required string Id { get; init; }
    /// <summary>Holded's sequential document number. Assigned at approval; empty on a draft.</summary>
    public required string DocNumber { get; init; }
    public required decimal Subtotal { get; init; }
    public required decimal Tax { get; init; }
    public required decimal Total { get; init; }
    public string? Status { get; init; }
    /// <summary>True while the document is still a draft: it books no revenue and carries no
    /// sequential number until <see cref="IHoldedClient.ApproveSalesDocumentAsync"/> runs. Null
    /// when Holded does not report the field.</summary>
    public bool? IsDraft { get; init; }
    /// <summary>The verbatim response body, kept for the audited payload on <c>store_invoices</c>.</summary>
    public required string RawJson { get; init; }
}
