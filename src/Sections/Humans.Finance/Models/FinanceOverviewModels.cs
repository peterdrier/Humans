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
    FinanceIncomeVm? Income,
    IReadOnlyList<FinanceSepaTransferVm> SepaInProgress)
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
    public IReadOnlyList<FinanceAccountLineVm> CashAccounts => [.. Banks, .. Stripe];
    public decimal CashTotal => Banks.Sum(a => a.Amount) + Stripe.Sum(a => a.Amount);
    public decimal ReceivablesTotal => Receivables.Sum(a => a.Amount);
}

/// <summary><see cref="Amount"/> is Holded's ledger balance. <see cref="Source"/> is set on an account whose provider
/// reports its own balance, <see cref="BankFeed"/> on the account Holded's bank feed is mirrored for.</summary>
internal sealed record FinanceAccountLineVm(
    int AccountNum, string Name, decimal Amount, FinanceAccountSourceVm? Source = null,
    FinanceBankFeedVm? BankFeed = null)
{
    public decimal? Difference => Source?.Balance is { } b ? b - Amount : null;
}

/// <summary>The provider's own balance, null when unknown, and the Finance action that reconciles the two.</summary>
internal sealed record FinanceAccountSourceVm(decimal? Balance, string ReconcileAction);

/// <summary>Bank lines of the last <see cref="Services.SepaBankBooking.FeedWindowDays"/> days that Holded has not fully
/// reconciled, as of <see cref="SyncedAt"/>. Both null when the feed has never been mirrored.</summary>
internal sealed record FinanceBankFeedVm(int? Unreconciled, Instant? SyncedAt);

/// <summary>A transfer sent to the bank in a SEPA file and not booked in Holded yet.</summary>
internal sealed record FinanceSepaTransferVm(Guid UserId, string FileName, Instant GeneratedAt, decimal Amount);

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

/// <summary><see cref="Problems"/> is null when the expense recon is unavailable. <see cref="DuplicateAccounts"/>
/// counts only accounts with duplicate documents that the recon does not already report.</summary>
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
