using System.Globalization;
using Humans.Base.Interfaces;
using Humans.Budget.Contracts;
using Humans.Expenses.Contracts;
using Humans.Finance.Contracts;
using Humans.Finance.Models;
using Humans.Holded.Contracts;
using Humans.Pleo.Contracts;
using NodaTime;

namespace Humans.Finance.Services;

/// <summary>/Finance's figures, composed from the pages that own each one: balances from the Holded ledger mirror,
/// creditors and recon from this section, the review queue from Expenses, plan and actuals from Budget and Cash flow,
/// the Pleo wallet from Pleo. Owns no tables.</summary>
internal sealed class FinanceOverviewService(
    IHoldedService holded,
    IFinanceService finance,
    IExpenseReportService expenseReports,
    IBudgetService budget,
    CashFlowService cashFlow,
    IPleoService pleo,
    IClock clock) : IApplicationService
{
    private static readonly DateTimeZone Madrid = DateTimeZoneProviders.Tzdb["Europe/Madrid"];

    public async Task<FinanceOverviewVm> GetAsync(Guid viewerUserId, CancellationToken ct = default)
    {
        var today = clock.GetCurrentInstant().InZone(Madrid).Date;

        var syncedAt = await holded.GetLedgerSyncedAtAsync(ct);
        FinanceLedgerCardsVm? ledger = null;
        if (syncedAt is not null)
        {
            var balances = await holded.GetAccountBalancesAsync(ct);
            var chart = await holded.GetChartAsync(ct);
            var filings = await TaxFilingsAsync(chart, today, ct);
            ledger = BuildLedgerCards(chart, balances, filings, (await pleo.GetWalletBalanceAsync(ct))?.CurrentEur);
        }

        var (creditors, _, creditorsUnavailable) = await finance.ListCreditorAccountsAsync(ct);
        var recon = await finance.GetExpenseReconAsync(null, ct);
        var queue = await expenseReports.GetReviewQueueAsync(viewerUserId, isFinanceAdmin: true, ct);
        var (transfers, _) = await finance.GetSepaTransfersAsync(ct);

        var year = await budget.GetActiveYearAsync(ct);
        FinanceBudgetVm? budgetVm = null;
        FinanceRunwayVm? runway = null;
        FinanceIncomeVm? income = null;
        if (year is not null)
        {
            if (int.TryParse(year.Year, NumberStyles.None, CultureInfo.InvariantCulture, out var calendarYear))
                budgetVm = BuildBudget(year, await finance.GetActualsForYearAsync(calendarYear, ct));

            var ticketSales = await cashFlow.GetGrossTicketRevenueAsync(year, ct);
            runway = BuildRunway(CashFlowService.Build(year, "monthly", ticketSales), today);
            income = BuildIncome(year, budget.ComputeBudgetSummary(year.Groups), ticketSales);
        }

        return new FinanceOverviewVm(
            syncedAt,
            ledger,
            BuildOwed(creditors, ledger?.TaxesOwed) with { Unavailable = creditorsUnavailable },
            BuildWork(queue, creditors, recon) with { CreditorsUnavailable = creditorsUnavailable },
            budgetVm,
            runway,
            income,
            BuildSepaInProgress(transfers));
    }

    private static bool Under(int accountNum, string prefix) =>
        accountNum.ToString(CultureInfo.InvariantCulture).StartsWith(prefix, StringComparison.Ordinal);

    private static readonly (string Prefix, string Name)[] ReceivableGroups =
    [
        ("430", "Customers (430)"),
        ("440", "Other debtors (440)"),
        ("470", "Hacienda and public bodies (470)"),
    ];

    /// <summary>Banks are 570/572, Stripe 441 until it moves. An archived account shows only while it holds money.
    /// Receivables are summed per group: 430 has one sub-account per customer. The Pleo account carries the wallet's
    /// own balance beside Holded's, unknown (null) before Pleo's first sweep.</summary>
    internal static FinanceLedgerCardsVm BuildLedgerCards(
        IReadOnlyList<HoldedChartAccountInfo> chart,
        IReadOnlyDictionary<int, decimal> balances,
        IReadOnlyList<FinanceTaxFilingVm> filings,
        decimal? pleoWallet = null)
    {
        decimal Balance(int num) => balances.GetValueOrDefault(num);

        List<FinanceAccountLineVm> Accounts(Func<int, bool> match) => chart
            .Where(a => match(a.Number) && (!a.Archived || Balance(a.Number) != 0m))
            .OrderBy(a => a.Number)
            .Select(a => new FinanceAccountLineVm(a.Number, a.Name, Balance(a.Number),
                a.Number == PleoBooking.PleoTreasury ? new FinanceAccountSourceVm(pleoWallet, "Pleo") : null))
            .ToList();

        var receivables = ReceivableGroups
            .Select(g => new FinanceAccountLineVm(
                int.Parse(g.Prefix, CultureInfo.InvariantCulture),
                g.Name,
                balances.Where(b => Under(b.Key, g.Prefix) && b.Value > 0m).Sum(b => b.Value)))
            .ToList();

        // Raw sign is Σdebit − Σcredit, so a liability's credit balance is negative.
        var taxesOwed = -balances.Where(b => Under(b.Key, "475")).Sum(b => b.Value);

        return new FinanceLedgerCardsVm(
            Accounts(n => Under(n, "570") || Under(n, "572")),
            Accounts(n => Under(n, "441")),
            receivables,
            taxesOwed,
            filings);
    }

    /// <summary>The return each model files next: last quarter's while its deadline has not passed, else the running
    /// quarter's so far.</summary>
    private async Task<IReadOnlyList<FinanceTaxFilingVm>> TaxFilingsAsync(
        IReadOnlyList<HoldedChartAccountInfo> chart, LocalDate today, CancellationToken ct)
    {
        async Task<List<HoldedLedgerLineInfo>> LinesAsync(string prefix)
        {
            var lines = new List<HoldedLedgerLineInfo>();
            foreach (var account in chart.Where(a => Under(a.Number, prefix)))
                lines.AddRange(await holded.GetLedgerLinesAsync(account.Number, ct));
            return lines;
        }

        var vatOut = await LinesAsync("477");
        var vatIn = await LinesAsync("472");
        var irpf = await LinesAsync("4751");
        var settlements = (await LinesAsync("4750")).Concat(await LinesAsync("4700"))
            .Select(l => l.EntryNumber).ToHashSet();
        var bankEntries = (await LinesAsync("570")).Concat(await LinesAsync("572"))
            .Select(l => l.EntryNumber).ToHashSet();

        return
        [
            TaxFiling("303", "VAT return", today, (from, to) => VatDue(vatOut, vatIn, settlements, from, to)),
            TaxFiling("111", "IRPF withholding", today, (from, to) => IrpfDue(irpf, bankEntries, from, to)),
        ];
    }

    /// <summary>Output VAT less input VAT, credit notes netted in. The quarter-end settlement into 4750/4700 is left
    /// out by entry: it zeroes 477 and 472 and would cancel the quarter.</summary>
    internal static decimal VatDue(IEnumerable<HoldedLedgerLineInfo> vatOut, IEnumerable<HoldedLedgerLineInfo> vatIn,
        IReadOnlySet<int> settlementEntries, LocalDate from, LocalDate to)
    {
        bool Counts(HoldedLedgerLineInfo l) => InRange(l, from, to) && !settlementEntries.Contains(l.EntryNumber);
        return vatOut.Where(Counts).Sum(l => l.Credit - l.Debit) - vatIn.Where(Counts).Sum(l => l.Debit - l.Credit);
    }

    /// <summary>Withholdings less corrections. A debit in an entry that moves a bank account is the payment of an
    /// earlier return, not a correction; a bank-paid payroll entry's withholding credit still counts.</summary>
    internal static decimal IrpfDue(IEnumerable<HoldedLedgerLineInfo> irpf, IReadOnlySet<int> bankEntries,
        LocalDate from, LocalDate to) =>
        irpf.Where(l => InRange(l, from, to))
            .Sum(l => l.Credit - (bankEntries.Contains(l.EntryNumber) ? 0m : l.Debit));

    private static bool InRange(HoldedLedgerLineInfo line, LocalDate from, LocalDate to)
    {
        var date = line.Date.InZone(Madrid).Date;
        return date >= from && date <= to;
    }

    internal static FinanceTaxFilingVm TaxFiling(
        string model, string name, LocalDate today, Func<LocalDate, LocalDate, decimal> amount)
    {
        var currentQuarter = (today.Month - 1) / 3 + 1;
        var (prevYear, prevQuarter) = currentQuarter == 1 ? (today.Year - 1, 4) : (today.Year, currentQuarter - 1);

        var (year, quarter, complete) = DueDate(model, prevYear, prevQuarter) >= today
            ? (prevYear, prevQuarter, true)
            : (today.Year, currentQuarter, false);

        var from = new LocalDate(year, (quarter - 1) * 3 + 1, 1);
        var to = from.PlusMonths(3).PlusDays(-1);
        return new FinanceTaxFilingVm(model, name, year, quarter, DueDate(model, year, quarter),
            amount(from, complete ? to : today), complete);
    }

    /// <summary>The 20th of the month after the quarter; Q4's 303 has until 30 January.</summary>
    internal static LocalDate DueDate(string model, int year, int quarter) => quarter == 4
        ? new LocalDate(year + 1, 1, string.Equals(model, "303", StringComparison.Ordinal) ? 30 : 20)
        : new LocalDate(year, quarter * 3 + 1, 20);

    /// <summary>A row bound to any member counts as a member's; unbound rows are suppliers. Debit balances (a
    /// prepayment) are left out: that is not owed by us.</summary>
    internal static FinanceOwedVm BuildOwed(IReadOnlyList<CreditorAccountRow> creditors, decimal? taxesOwed)
    {
        var owing = creditors.Where(r => r.OwedToMember > 0m).ToList();
        var members = owing.Where(r => r.Bindings.Count > 0).ToList();
        var suppliers = owing.Where(r => r.Bindings.Count == 0).ToList();
        return new FinanceOwedVm(
            members.Count,
            members.Sum(r => r.OwedToMember),
            members.Where(r => r.PendingPayoutFileName is not null).Sum(r => r.OwedToMember),
            suppliers.Count,
            suppliers.Sum(r => r.OwedToMember),
            taxesOwed);
    }

    /// <summary>Sent to the bank, not booked and not withdrawn, oldest first.</summary>
    internal static IReadOnlyList<FinanceSepaTransferVm> BuildSepaInProgress(
        IReadOnlyList<SepaPayoutTransferRow> transfers) => transfers
        .Where(t => !t.IsBooked && !t.IsWithdrawn)
        .OrderBy(t => t.GeneratedAt)
        .Select(t => new FinanceSepaTransferVm(t.UserId, t.FileName, t.GeneratedAt, t.Amount))
        .ToList();

    /// <summary>Ready to pay uses the same service-derived eligibility as the Creditors page and payout planning.</summary>
    internal static FinanceWorkVm BuildWork(
        IReadOnlyList<ExpenseReportDto> queue,
        IReadOnlyList<CreditorAccountRow> creditors,
        ExpenseReconReport recon)
    {
        var submitted = queue.Where(r => r.Status == ExpenseReportStatus.Submitted).ToList();
        var endorsed = queue.Where(r => r.Status == ExpenseReportStatus.CoordinatorEndorsed).ToList();
        var ready = creditors
            .Where(r => r.NotPayableReason is null)
            .ToList();
        // The recon and the duplicate-document check see the same doubled purchase from two mirrors: an account the
        // recon already names is not a second problem.
        var reconDuplicateAccounts = recon.Lines
            .Where(l => l.Status == ExpenseReconLineStatus.Duplicated && l.LedgerAccountNum is not null)
            .Select(l => l.LedgerAccountNum!.Value)
            .ToHashSet();
        var duplicates = creditors.Count(r =>
            r.HasDuplicateDocuments == true && !reconDuplicateAccounts.Contains(r.SupplierAccountNum));
        int? reconProblems = recon.IsAvailable ? recon.Totals.Problems : null;

        return new FinanceWorkVm(
            submitted.Count,
            submitted.Min(r => r.SubmittedAt),
            endorsed.Count,
            endorsed.Min(r => r.CoordinatorEndorsedAt),
            ready.Count,
            ready.Sum(r => r.OwedToMember),
            creditors.Count(r => r.PendingPayoutFileName is not null),
            reconProblems is { } p ? p + duplicates : null,
            duplicates,
            reconProblems);
    }

    /// <summary>Expense categories only (a negative allocation). Spent is the Holded actual, gross like the budget
    /// pages.</summary>
    internal static FinanceBudgetVm BuildBudget(BudgetYearDetail year, IReadOnlyList<CategoryActualRow> actuals)
    {
        var actualByCategory = actuals.ToDictionary(a => a.BudgetCategoryId, a => Math.Abs(a.Actual));
        // Group priority breaks equal-overage ties in the top-three selector.
        var categories = year.Groups.OrderBy(group => group.SortOrder)
            .SelectMany(g => g.Categories).Where(c => c.AllocatedAmount < 0m).ToList();

        var over = categories
            .Select(c => new FinanceOverBudgetVm(
                c.Id, c.Name, actualByCategory.GetValueOrDefault(c.Id) - Math.Abs(c.AllocatedAmount)))
            .Where(o => o.Over > 0m)
            .OrderByDescending(o => o.Over)
            .Take(3)
            .ToList();

        return new FinanceBudgetVm(
            year.Id,
            year.Name,
            categories.Sum(c => Math.Abs(c.AllocatedAmount)),
            categories.Sum(c => actualByCategory.GetValueOrDefault(c.Id)),
            over);
    }

    internal static FinanceRunwayVm BuildRunway(CashFlowViewModel plan, LocalDate today)
    {
        var thisMonth = new LocalDate(today.Year, today.Month, 1);
        decimal Spend(LocalDate month) =>
            -plan.Periods.Where(p => p.PeriodStart == month).Sum(p => p.ExpenseTotal);

        return new FinanceRunwayVm(
            Spend(thisMonth),
            Spend(thisMonth.PlusMonths(1)),
            plan.Periods.FirstOrDefault(p => p.FundsExhausted)?.Label);
    }

    internal static FinanceIncomeVm BuildIncome(BudgetYearDetail year, BudgetSummaryResult summary, decimal ticketSales) =>
        new(
            ticketSales,
            year.Groups.Where(g => g.IsTicketingGroup)
                .SelectMany(g => g.Categories)
                .Where(c => c.AllocatedAmount > 0m)
                .Sum(c => c.AllocatedAmount),
            summary.TotalIncome);
}
