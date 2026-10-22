using Humans.Base.Interfaces;

namespace Humans.Store.Contracts;

/// <summary>
/// Read-only. Line amounts are the effective prices <c>/Store/Admin/Summary</c> shows, so an order's
/// <see cref="AccountingOrderLineDto.LineGross"/> rows sum to the summary's total. Orders are selected by persisted
/// year, not counterparty, so a row whose camp or team is gone is still exported as <c>(unknown camp)</c> /
/// <c>(unknown team)</c>.
/// </summary>
public interface IStoreAccountingRead : IApplicationService
{
    /// <summary>Camp and team orders.</summary>
    Task<IReadOnlyList<AccountingOrderLineDto>> GetOrderLinesAsync(int year, CancellationToken ct = default);

    /// <summary><c>Paid</c> payments only; refunds are negative.</summary>
    Task<IReadOnlyList<AccountingPaymentDto>> GetPaymentsAsync(int year, CancellationToken ct = default);
}
