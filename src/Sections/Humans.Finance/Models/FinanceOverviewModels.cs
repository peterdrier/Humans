using NodaTime;

namespace Humans.Finance.Models;

/// <summary>/Finance. Amounts are display-signed: positive is cash held, money owed by us, or money owed to us,
/// whichever the card is about. A null card could not be computed and says why in its own reason.</summary>
internal sealed record FinanceOverviewVm(
    Instant? LedgerSyncedAt,
    FinanceLedgerCardsVm? Ledger,
    FinanceOwedVm Owed,
    FinanceWorkVm Work,
    FinanceBudgetVm? Budget,
    FinanceRunwayVm? Runway,
    FinanceIncomeVm? Income)
{
    /// <summary>Cash plus what others owe us, less what we owe. Null until every part is known.</summary>
    public decimal? NetPosition => Ledger is { } l && Owed.Total is { } owed
        ? l.CashTotal + l.ReceivablesTotal - owed
        : null;
}

/// <summary>Everything read straight off the ledger mirror's balances.</summary>
internal sealed record FinanceLedgerCardsVm(
    IReadOnlyList<FinanceAccountLineVm> Banks,
    IReadOnlyList<FinanceAccountLineVm> Stripe,
    IReadOnlyList<FinanceAccountLineVm> Receivables,
    decimal TaxesOwed,
    IReadOnlyList<FinanceTaxFilingVm> TaxFilings)
{
    public decimal CashTotal => Banks.Sum(a => a.Amount) + Stripe.Sum(a => a.Amount);
    public decimal ReceivablesTotal => Receivables.Sum(a => a.Amount);
}

internal sealed record FinanceAccountLineVm(int AccountNum, string Name, decimal Amount);

/// <summary>A quarterly return. <see cref="IsComplete"/> false: the quarter is still running, so the amount is so
/// far.</summary>
internal sealed record FinanceTaxFilingVm(string Model, string Name, int Year, int Quarter, LocalDate DueDate,
    decimal Amount, bool IsComplete);

/// <summary>Creditor accounts (40000000-41999999) plus the 475 tax accounts. <see cref="Total"/> is null when the ledger
/// mirror never synced, since the tax part is then unknown, or when <see cref="Unavailable"/> says the creditor accounts
/// could not be listed.</summary>
internal sealed record FinanceOwedVm(
    int MemberAccounts,
    decimal Members,
    decimal InSepaFiles,
    int SupplierAccounts,
    decimal Suppliers,
    decimal? Taxes,
    string? Unavailable = null)
{
    public decimal? Total => Unavailable is null && Taxes is { } t ? Members + Suppliers + t : null;
}

/// <summary><see cref="Problems"/> is null when the expense recon is unavailable.</summary>
internal sealed record FinanceWorkVm(
    int AwaitingCoordinator,
    Instant? OldestAwaitingCoordinator,
    int AwaitingFinance,
    Instant? OldestAwaitingFinance,
    int ReadyToPay,
    decimal ReadyToPayAmount,
    int AwaitingBooking,
    int? Problems,
    int DuplicateAccounts,
    int? ReconProblems,
    string? CreditorsUnavailable = null);

/// <summary>Expense categories of the active year only; actuals are the attributed Holded documents.</summary>
internal sealed record FinanceBudgetVm(
    Guid YearId,
    string YearName,
    decimal Planned,
    decimal Spent,
    IReadOnlyList<FinanceOverBudgetVm> OverBudget)
{
    public int SpentPercent => Planned > 0m ? (int)Math.Round(Spent / Planned * 100m) : 0;
}

internal sealed record FinanceOverBudgetVm(Guid CategoryId, string Name, decimal Over);

/// <summary>From the cash-flow plan. <see cref="FundsRunOut"/> is the period label, null when the plan never runs
/// out.</summary>
internal sealed record FinanceRunwayVm(decimal ThisMonth, decimal NextMonth, string? FundsRunOut);

internal sealed record FinanceIncomeVm(decimal TicketSalesPaid, decimal TicketingTarget, decimal BudgetedIncome);
