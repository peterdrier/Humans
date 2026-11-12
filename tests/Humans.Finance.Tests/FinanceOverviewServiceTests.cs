using AwesomeAssertions;
using Humans.Budget.Contracts;
using Humans.Finance.Contracts;
using Humans.Finance.Models;
using Humans.Finance.Services;
using Humans.Holded.Contracts;
using NodaTime;
using Xunit;

namespace Humans.Finance.Tests;

public class FinanceOverviewServiceTests
{
    [HumansFact]
    public void TaxFiling_ShowsLastQuarterUntilItsDeadline_ThenTheRunningQuarter()
    {
        var before = FinanceOverviewService.TaxFiling("303", "VAT", new LocalDate(2027, 1, 30), (_, _) => 0m);
        before.Should().Match<FinanceTaxFilingVm>(f =>
            f.Year == 2026 && f.Quarter == 4 && f.IsComplete && f.DueDate == new LocalDate(2027, 1, 30));

        (LocalDate From, LocalDate To) window = default;
        var after = FinanceOverviewService.TaxFiling("111", "IRPF", new LocalDate(2027, 1, 21),
            (from, to) => { window = (from, to); return 0m; });
        after.Should().Match<FinanceTaxFilingVm>(f =>
            f.Year == 2027 && f.Quarter == 1 && !f.IsComplete && f.DueDate == new LocalDate(2027, 4, 20));
        window.Should().Be((new LocalDate(2027, 1, 1), new LocalDate(2027, 1, 21)));
    }

    [HumansFact]
    public void TaxAmounts_NetCreditNotes_AndSkipSettlementAndPayment()
    {
        var day = new LocalDate(2026, 11, 2).AtStartOfDayInZone(DateTimeZoneProviders.Tzdb["Europe/Madrid"]).ToInstant();
        HoldedLedgerLineInfo Line(int entry, int account, decimal debit, decimal credit) =>
            new(entry, 1, account, day, null, null, debit, credit);
        var (from, to) = (new LocalDate(2026, 10, 1), new LocalDate(2026, 12, 31));

        var vat = FinanceOverviewService.VatDue(
            [Line(1, 47700000, 0m, 100m), Line(2, 47700000, 10m, 0m), Line(4, 47700000, 90m, 0m)],
            [Line(3, 47200000, 30m, 0m), Line(4, 47200000, 0m, 30m)],
            new HashSet<int> { 4 }, from, to);
        vat.Should().Be(60m);

        var irpf = FinanceOverviewService.IrpfDue(
            [Line(5, 47510000, 0m, 50m), Line(6, 47510000, 5m, 0m), Line(7, 47510000, 40m, 0m)],
            new HashSet<int> { 7 }, from, to);
        irpf.Should().Be(45m);
    }

    [HumansFact]
    public void BuildLedgerCards_GroupsCashAndReceivables_AndFlipsTaxes()
    {
        HoldedChartAccountInfo Account(int n, bool archived = false) => new(n, $"A{n}", null, null, archived, 0m);
        var chart = new[]
        {
            Account(57200001), Account(57200002, archived: true), Account(57200003, archived: true),
            Account(44100001), Account(44000001), Account(47500001),
        };
        var balances = new Dictionary<int, decimal>
        {
            [57200001] = 100m,
            [57200003] = 5m,
            [44100001] = 20m,
            [44000001] = 7m,
            [43000001] = 3m,
            [43000002] = -1m,
            [47500001] = -40m,
        };

        var cards = FinanceOverviewService.BuildLedgerCards(chart, balances, [], pleoWallet: 2m);

        cards.Banks.Select(a => a.AccountNum).Should().Equal(57200001, 57200003);
        cards.Stripe.Select(a => a.AccountNum).Should().Equal(44100001);
        cards.CashTotal.Should().Be(125m);
        cards.Receivables.Select(r => r.Amount).Should().Equal(3m, 7m, 0m);
        cards.TaxesOwed.Should().Be(40m);
        cards.CashAccounts.Where(a => a.Source is not null).Should().ContainSingle()
            .Which.Should().Match<FinanceAccountLineVm>(a => a.AccountNum == 57200003 && a.Difference == -3m);
    }

    [HumansFact]
    public void BuildSepaInProgress_KeepsUnbookedUnwithdrawn_OldestFirst()
    {
        static SepaPayoutTransferRow Transfer(decimal amount, long sentAt, Instant? booked = null, Instant? withdrawn = null) =>
            new(Guid.NewGuid(), Guid.NewGuid(), "f.xml", Instant.FromUnixTimeSeconds(sentAt), Guid.NewGuid(),
                Guid.NewGuid(), 41000001, null, "N", "ES**", amount, booked, null, null, null, null, null, null,
                WithdrawnAt: withdrawn);
        var now = Instant.FromUnixTimeSeconds(9);

        var open = FinanceOverviewService.BuildSepaInProgress(
            [Transfer(1m, 2), Transfer(2m, 1), Transfer(3m, 0, booked: now), Transfer(4m, 0, withdrawn: now)]);

        open.Select(t => t.Amount).Should().Equal(2m, 1m);
    }

    [HumansFact]
    public void BuildOwed_And_BuildWork_SplitMembersSuppliersAndPayability()
    {
        static CreditorContactBinding Bind() => new(Guid.NewGuid(), "c", null, CreditorContactSource.Auto);
        var rows = new[]
        {
            Row(40000001, 50m, [Bind()]),                              // ready to pay
            Row(40000002, 30m, [Bind()], pending: "f.xml", reason: "in flight"), // in a SEPA file
            Row(40000003, 20m, [Bind(), Bind()], reason: "collision"), // collision: owed, not payable
            Row(40000004, 10m, [Bind()], duplicates: true, reason: "duplicates"), // duplicate docs
            Row(40000005, 70m, [], reason: "unbound"), // supplier
            Row(40000006, -5m, [], reason: "nothing owed"), // prepayment: not owed
        };

        var owed = FinanceOverviewService.BuildOwed(rows, taxesOwed: 9m);
        owed.Should().Be(new FinanceOwedVm(4, 110m, 30m, 1, 70m, 9m));
        owed.Total.Should().Be(189m);
        (owed with { Unavailable = "contacts unreadable" }).Total.Should().BeNull("an unknown creditor total is not zero");

        var recon = new ExpenseReconReport(null, null, null, [], [], [], [], new ExpenseReconTotals(0, 0, 0, 2, 0, 0m, 1, 0));
        var work = FinanceOverviewService.BuildWork([], [.. rows,
            Row(40000007, 60m, [Bind()], reason: "no IBAN")], recon);
        work.ReadyToPay.Should().Be(1);
        work.ReadyToPayAmount.Should().Be(50m);
        work.AwaitingBooking.Should().Be(1);
        work.Problems.Should().Be(4);

        // The recon already names 40000004's doubled purchase: the account is not counted again.
        var line = new ExpenseReconLine(Guid.NewGuid(), null, Guid.NewGuid(), "M", "d", 10m, new LocalDate(2026, 1, 1), "",
            ExpenseReconLineStatus.Duplicated, null, 40000004, 1, null, [2], 10m);
        FinanceOverviewService.BuildWork([], rows, recon with { Lines = [line] }).Problems.Should().Be(3);
    }

    [HumansFact]
    public void BuildBudget_ComparesExpenseCategoriesOnly()
    {
        var yearId = Guid.NewGuid();
        var sound = Category(yearId, "Sound", -100m);
        var bar = Category(yearId, "Bar", -200m);
        var tickets = Category(yearId, "Tickets", 1000m);
        var year = new BudgetYearDetail(yearId, "2026", "2026", BudgetYearStatus.Active, false,
            [new BudgetGroupDetail(Guid.NewGuid(), yearId, "G", 0, false, false, false, null, [sound, bar, tickets])]);

        var budget = FinanceOverviewService.BuildBudget(year,
            [new CategoryActualRow(sound.Id, 130m, []), new CategoryActualRow(tickets.Id, 900m, [])]);

        budget.Planned.Should().Be(300m);
        budget.Spent.Should().Be(130m);
        budget.OverBudget.Should().ContainSingle().Which.Should().Be(new FinanceOverBudgetVm(sound.Id, "Sound", 30m));
    }

    private static CreditorAccountRow Row(int num, decimal owed, IReadOnlyList<CreditorContactBinding> bindings,
        string? pending = null, bool duplicates = false, string? reason = null) =>
        new(num, $"N{num}", -owed, Math.Max(0m, owed), bindings, PendingPayoutFileName: pending,
            HasDuplicateDocuments: duplicates)
        { NotPayableReason = reason };

    private static BudgetCategoryDetail Category(Guid yearId, string name, decimal allocated) =>
        new(Guid.NewGuid(), yearId, name, allocated, ExpenditureType.OpEx, null, 0, []);
}
