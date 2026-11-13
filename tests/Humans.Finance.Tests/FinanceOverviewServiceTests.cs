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

        var feed = new FinanceBankFeedVm(3, Instant.FromUtc(2026, 1, 1, 0, 0));
        var cards = FinanceOverviewService.BuildLedgerCards(chart, balances, [], pleoWallet: 2m, 57200001, feed);

        cards.Banks.Select(a => a.BankFeed).Should().Equal(feed, null);

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
        var sent = new SepaPayoutTransferRow(
            Guid.NewGuid(), Guid.NewGuid(), "f.xml", Instant.FromUtc(2026, 3, 1, 9, 0), Guid.NewGuid(), Guid.NewGuid(),
            40000002, null, "Ana", "ES**1234", 30m, null, null, null, null, null, null, null);
        var pending = new ExpenseReconLine(Guid.NewGuid(), null, Guid.NewGuid(), "M", "p", 5m, new LocalDate(2026, 1, 1),
            "Queued", ExpenseReconLineStatus.Pending, null, null, null, null, [], 0m);
        var work = FinanceOverviewService.BuildWork([], [.. rows,
            Row(40000007, 60m, [Bind()], reason: "no IBAN")], recon with { Lines = [pending] },
            [sent, sent with { CandidateBankMovementId = "m1" },
                sent with { BatchLine = new SepaBatchLineVm("m2", new LocalDate(2026, 3, 2), -60m, null, false, null) },
                sent with { BookedAt = sent.GeneratedAt },
                sent with { WithdrawnAt = sent.GeneratedAt }]);
        work.ReadyToPay.Should().Be(1);
        work.ReadyToPayAmount.Should().Be(50m);
        work.PushingToHolded.Should().Be(1);
        work.AwaitingBankLine.Should().Be(1);
        work.TransfersToBook.Should().Be(2);
        work.Problems.Should().Be(4);

        // The recon already names 40000004's doubled purchase: the account is not counted again.
        var line = new ExpenseReconLine(Guid.NewGuid(), null, Guid.NewGuid(), "M", "d", 10m, new LocalDate(2026, 1, 1), "",
            ExpenseReconLineStatus.Duplicated, null, 40000004, 1, null, [2], 10m);
        FinanceOverviewService.BuildWork([], rows, recon with { Lines = [line] }, []).Problems.Should().Be(3);

        // An unreadable bank feed leaves the split unknown while a transfer is open, never zero.
        FinanceOverviewService.BuildWork([], rows, recon, [sent], "feed unreadable").TransfersToBook.Should().BeNull();
        FinanceOverviewService.BuildWork([], rows, recon, [], "feed unreadable").TransfersToBook.Should().Be(0);
    }

    [HumansFact]
    public void CreditorMatch_IsTheAccountsSliceOfTheExpenseRecon()
    {
        var user = Guid.NewGuid();
        var transfer = new ExpenseReconOpenTransfer(Guid.NewGuid(), "f.xml", Instant.FromUtc(2026, 1, 1, 0, 0), 5m);
        var member = new ExpenseReconMember(user, "M", 40000001, -20m, -50m, 30m, -10m, MissingLines: 1,
            UnclaimedPurchaseCount: 1, [transfer], [new(new LocalDate(2026, 1, 2), 9, "entry", null, 10m)],
            ExpenseReconMemberStatus.Off, []);
        var duplicated = new ExpenseReconLine(Guid.NewGuid(), null, user, "M", "d", 10m, new LocalDate(2026, 1, 1), "",
            ExpenseReconLineStatus.Duplicated, null, 40000001, 1, null, [2], 10m);
        var orphan = new ExpenseReconOrphan(40000009, 3, new LocalDate(2026, 1, 3), "ER: x", 7m,
            ExpenseReconOrphanKind.UnboundContact);
        var recon = new ExpenseReconReport(null, Instant.FromUtc(2026, 1, 4, 0, 0), null, [], [duplicated], [orphan],
            [member], new ExpenseReconTotals(0, 0, 0, 0, 0, 0m, 0, 0));

        // The member's side: positive is owed to the member.
        CreditorMatchVm.For(recon, 40000001).Should().BeEquivalentTo(
            new CreditorMatchVm(50m, -30m, 10m, -10m, 1, 1, 1, 1));
        CreditorMatchVm.For(recon, 40000001)!.Warnings.Should().HaveCount(4);
        // A second member on the same account: the ledger split is the account's, their Missing line still counts.
        var shared = member with { UserId = Guid.NewGuid(), MissingLines = 2 };
        CreditorMatchVm.For(recon with { Members = [member, shared] }, 40000001).Should().BeEquivalentTo(
            new CreditorMatchVm(50m, -30m, 10m, -10m, 3, 1, 1, 1));
        // No member, but an expense purchase nobody claims.
        CreditorMatchVm.For(recon, 40000009).Should().BeEquivalentTo(new CreditorMatchVm(0m, 0m, 7m, 0m, 0, 1, 0, 0));
        CreditorMatchVm.For(recon, 40000002).Should().BeNull("the recon has nothing on the account");
        CreditorMatchVm.For(recon with { UnavailableReason = "never synced" }, 40000001).Should().BeNull();
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

    [HumansFact]
    public void BuildBudget_UsesGroupSortOrderToBreakEqualOverBudgetTies()
    {
        var yearId = Guid.NewGuid();
        var groups = Enumerable.Range(0, 4).Select(sortOrder =>
        {
            var id = Guid.NewGuid();
            return new BudgetGroupDetail(id, yearId, $"Group {sortOrder}", sortOrder, false, false, false, null,
                [Category(id, $"Category {sortOrder}", -100m)]);
        }).ToList();
        var year = new BudgetYearDetail(yearId, "2026", "2026", BudgetYearStatus.Active, false,
            groups.AsEnumerable().Reverse().ToList());
        var actuals = groups.Select(group => new CategoryActualRow(
            group.Categories[0].Id, group.SortOrder == 3 ? 150m : 125m, [])).ToList();

        var budget = FinanceOverviewService.BuildBudget(year, actuals);

        budget.OverBudget.Select(row => row.CategoryId).Should().Equal(
            groups[3].Categories[0].Id, groups[0].Categories[0].Id, groups[1].Categories[0].Id);
    }

    private static CreditorAccountRow Row(int num, decimal owed, IReadOnlyList<CreditorContactBinding> bindings,
        string? pending = null, bool duplicates = false, string? reason = null) =>
        new(num, $"N{num}", -owed, Math.Max(0m, owed), bindings, PendingPayoutFileName: pending,
            HasDuplicateDocuments: duplicates)
        { NotPayableReason = reason };

    private static BudgetCategoryDetail Category(Guid yearId, string name, decimal allocated) =>
        new(Guid.NewGuid(), yearId, name, allocated, ExpenditureType.OpEx, null, 0, []);
}
