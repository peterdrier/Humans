using Humans.Store.Domain;
using Humans.Store.Services.Dtos;

namespace Humans.Store.Models;

internal sealed class IndexViewModel
{
    public int Year { get; init; }
    public IReadOnlyList<ProductDto> Catalog { get; init; } = [];
    public IReadOnlyList<CounterpartyOrders> Counterparties { get; init; } = [];
    /// <summary>
    /// Keyed by counterparty: may the viewer Create (no order) or Delete (has one). Resolved per row, since privileged
    /// readers see counterparties they cannot manage.
    /// </summary>
    public IReadOnlyDictionary<Guid, bool> CanManageByCounterparty { get; init; } =
        new Dictionary<Guid, bool>();
}

internal sealed class OrderViewModel
{
    public OrderDto Order { get; init; } = null!;
    public IReadOnlyList<ProductDto> Catalog { get; init; } = [];
    public string CounterpartyDisplayName { get; init; } = string.Empty;
    public bool CanEdit { get; init; }
    public bool CanPay { get; init; }
    public bool IsStripeConfigured { get; init; }
    public bool CanDelete { get; init; }

    public bool CanIssueInvoice { get; init; }

    public bool CanRecordPayment { get; init; }

    public bool CanRefund { get; init; }

    public bool CanDeletePayment { get; init; }

    public decimal DepositsReturnedEur => Order.Payments
        .Where(p => p.Method == PaymentMethod.DepositReturn && p.Status == PaymentStatus.Paid)
        .Sum(p => p.AmountEur);

    /// <summary>Negative balance: the org owes the camp.</summary>
    public bool IsRefundDue => Order.BalanceEur < 0;

    public decimal BalanceBoxAmountEur => Math.Abs(Order.BalanceEur);

    public IReadOnlyCollection<Guid> RemovableLineIds { get; init; } = [];

    public static OrderViewModel FromPageData(
        OrderPageData pageData,
        bool canDelete,
        bool canIssueInvoice,
        bool canRecordPayment,
        bool canRefund,
        bool canDeletePayment,
        IReadOnlyList<ProductDto> catalog,
        IReadOnlyCollection<Guid> removableLineIds) => new()
        {
            CanIssueInvoice = canIssueInvoice,
            CanRecordPayment = canRecordPayment,
            CanRefund = canRefund,
            CanDeletePayment = canDeletePayment,
            Order = pageData.Order,
            Catalog = catalog,
            CounterpartyDisplayName = pageData.CounterpartyDisplayName,
            CanEdit = pageData.CanEdit,
            RemovableLineIds = removableLineIds,
            CanPay = pageData.CanPay,
            IsStripeConfigured = pageData.IsStripeConfigured,
            CanDelete = canDelete
        };
}
