using Humans.Store.Domain;
using Humans.Store.Services;
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
    public bool CanEdit { get; init; }
    public bool CanPay { get; init; }
    public bool IsStripeConfigured { get; init; }
    public bool CanDelete { get; init; }

    public bool CanIssueInvoice { get; init; }

    public bool CanRecordPayment { get; init; }

    public bool CanRecordFinancePayment { get; init; }

    public bool CanDeletePayment { get; init; }

    public decimal DepositsReturnedEur => Order.Payments
        .Where(p => p.Method == PaymentMethod.DepositReturn && p.Status == PaymentStatus.Paid)
        .Sum(p => p.AmountEur);

    /// <summary>Negative balance: the org owes the camp.</summary>
    public bool IsRefundDue => Order.BalanceEur < 0;

    public decimal BalanceBoxAmountEur => Math.Abs(Order.BalanceEur);

    public IReadOnlyCollection<Guid> RemovableLineIds { get; init; } = [];

    /// <summary>Stripe dashboard page per paid Stripe payment id; filled only for refund-capable viewers.</summary>
    public IReadOnlyDictionary<Guid, string> StripeRefundUrls { get; set; } = new Dictionary<Guid, string>();

    /// <summary>What issuing would invoice and why it may not yet; set only for viewers who can issue.</summary>
    public StoreInvoicePlan? InvoicePlan { get; set; }

    /// <summary>The issued invoice with its booking state; null before issuance.</summary>
    public Invoice? Invoice { get; set; }

    /// <summary>The order's products that carry a deposit, for a deposit return to name.</summary>
    public IReadOnlyList<(Guid ProductId, string Name)> DepositProducts => Order.Lines
        .Where(l => l.EffectiveDeposit is > 0m)
        .Select(l => (l.ProductId, l.ProductName))
        .Distinct()
        .ToList();


    public static OrderViewModel FromPageData(
        OrderPageData pageData,
        bool canDelete,
        bool canIssueInvoice,
        bool canRecordPayment,
        bool canRecordFinancePayment,
        bool canDeletePayment,
        IReadOnlyList<ProductDto> catalog,
        IReadOnlyCollection<Guid> removableLineIds) => new()
        {
            CanIssueInvoice = canIssueInvoice,
            CanRecordPayment = canRecordPayment,
            CanRecordFinancePayment = canRecordFinancePayment,
            CanDeletePayment = canDeletePayment,
            Order = pageData.Order,
            Catalog = catalog,
            CanEdit = pageData.CanEdit,
            RemovableLineIds = removableLineIds,
            CanPay = pageData.CanPay,
            IsStripeConfigured = pageData.IsStripeConfigured,
            CanDelete = canDelete
        };
}
