using Humans.Base.Interfaces;

namespace Humans.Store.Contracts;

/// <summary>
/// Store's read-only accounting surface for the machine API behind
/// <c>/api/backdoor/store</c> (peterdrier/Humans#1719): one row per order line with its
/// effective price / VAT / revenue-account, and one row per settled payment with its Stripe
/// payment intent id, both for one event year. Finance splits Holded's one-line-per-payment
/// Stripe feed by product with it.
/// </summary>
/// <remarks>
/// Read-only by design — nothing in Store is written over the API. Line amounts are the
/// same effective prices <c>/Store/Admin/Summary</c> shows (live catalog for an Open order,
/// frozen snapshot once InvoiceIssued), so an order's <see cref="AccountingOrderLineDto.LineGross"/>
/// rows sum to the total the summary reports. Orders are selected by their persisted year,
/// not through the counterparty, so a row whose camp or team has since gone is still
/// exported, labelled <c>(unknown camp)</c> / <c>(unknown team)</c>. Labels are stitched
/// here, never by the caller.
/// </remarks>
public interface IStoreAccountingRead : IApplicationService
{
    /// <summary>Every line on every camp and team order of <paramref name="year"/>.</summary>
    Task<IReadOnlyList<AccountingOrderLineDto>> GetOrderLinesAsync(int year, CancellationToken ct = default);

    /// <summary>Every <c>Paid</c> payment against a camp order of <paramref name="year"/>; refunds are negative.</summary>
    Task<IReadOnlyList<AccountingPaymentDto>> GetPaymentsAsync(int year, CancellationToken ct = default);
}
