using AwesomeAssertions;
using Humans.Base.Caching;
using Humans.Base.Interfaces;
using Humans.AuditLog.Contracts;
using Humans.Budget.Contracts;
using Humans.Email.Contracts;
using Humans.Expenses.Contracts;
using Humans.Finance.Contracts;
using Humans.Finance.Data;
using Humans.Finance.Domain;
using Humans.Finance.Jobs;
using Humans.Finance.Services;
using Humans.Holded.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Humans.Finance.Tests;

/// <summary>/Finance/ExpenseRecon: approved lines against the seeded ledger mirror.</summary>
public class ExpenseReconTests
{
    private static readonly Instant SyncedAt = Instant.FromUtc(2026, 10, 29, 2, 45);
    private static readonly Instant Submitted = Instant.FromUtc(2026, 10, 10, 9, 0);
    private static readonly Guid Alice = Guid.NewGuid();
    private static readonly Guid Bob = Guid.NewGuid();
    private static readonly Guid ReportA = Guid.NewGuid();
    private const int AliceAccount = 41000001;
    private const int BobAccount = 41000002;
    private const int TwinAccount = 41000009;
    private static int _entry = 1000;

    private static ExpenseHoldedReconLine Line(
        string? docId = "d1", decimal amount = 100m, string description = "ER: Train", Guid? user = null,
        Guid? report = null, ExpenseHoldedSyncState state = ExpenseHoldedSyncState.Pushed, Instant? settled = null,
        Instant? date = null) =>
        new(report ?? ReportA, Guid.NewGuid(), ExpenseReportStatus.Approved, user ?? Alice, "c-alice", docId, description,
            $"Expense report {report ?? ReportA}", amount, date ?? Submitted, state, settled ?? Submitted);

    private static HoldedLedgerLineInfo Ledger(
        int account, string? type, string? description, decimal debit = 0m, decimal credit = 0m,
        LocalDate? date = null) =>
        new(Interlocked.Increment(ref _entry), 1, account,
            (date ?? new LocalDate(2026, 10, 10)).AtStartOfDayInZone(DateTimeZoneProviders.Tzdb["Europe/Madrid"]).ToInstant(),
            type, description, debit, credit);

    /// <summary>Holded's own text for a document's line.</summary>
    private static HoldedLedgerLineInfo Purchase(
        decimal amount = 100m, string description = "ER: Train", int account = AliceAccount, LocalDate? date = null) =>
        Ledger(account, "purchase", $"Purchase, Alice Example, {description}, 10/10/2026", credit: amount, date: date);

    private static SepaPayoutTransferRow Transfer(int account, decimal amount, bool booked, bool withdrawn = false) => new(
        Guid.NewGuid(), Guid.NewGuid(), "payout-1.xml", Instant.FromUtc(2026, 10, 12, 9, 0), Guid.NewGuid(), Alice,
        account, "c-alice", "ALICE", "ES** **** 1234", amount, booked ? Instant.FromUtc(2026, 10, 15, 9, 0) : null,
        null, null, null, null, null, null, WithdrawnAt: withdrawn ? Submitted : null);

    private static readonly Dictionary<Guid, int> Accounts = new() { [Alice] = AliceAccount, [Bob] = BobAccount };

    private static ExpenseReconReport Build(
        IReadOnlyList<ExpenseHoldedReconLine> lines, params HoldedLedgerLineInfo[] ledger) =>
        Build(lines, ledger, []);

    private static ExpenseReconReport Build(
        IReadOnlyList<ExpenseHoldedReconLine> lines, IReadOnlyList<HoldedLedgerLineInfo> ledger,
        IReadOnlyList<SepaPayoutTransferRow> transfers, IReadOnlyDictionary<Guid, int>? accounts = null,
        int? year = null) =>
        ExpenseRecon.Build(year, SyncedAt, lines, accounts ?? Accounts,
            ledger.GroupBy(l => l.AccountNum)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<HoldedLedgerLineInfo>)g.ToList()),
            transfers, id => id == Alice ? "Alice" : "Bob");

    [HumansFact]
    public void LineMatchesItsLedgerPurchase_WhateverTheStoredDocIdPointsAt()
    {
        var report = Build([Line("deleted-by-cleanup")], Purchase());

        report.Lines.Should().ContainSingle().Which.Should().Match<ExpenseReconLine>(l =>
            l.Status == ExpenseReconLineStatus.Booked && l.LedgerAccountNum == AliceAccount);
        report.Members.Should().ContainSingle().Which.Status.Should().Be(ExpenseReconMemberStatus.Balanced);
        report.Totals.Should().Be(new ExpenseReconTotals(1, 1, 1, 0, 0, 0m, 0, 0));
    }

    [HumansFact]
    public void Descriptions_AreComparedTrimmed_WithRunsOfSpacesCollapsed()
    {
        var report = Build(
            [
                Line(description: "ER: Action. Cable, lights, screws, etc. "),
                Line(description: "ER:  Spot lights"),
                Line(description: "ER: 1 m  SEÑALIZACIÓN"),
            ],
            Purchase(description: "ER: Action. Cable, lights, screws, etc."),
            Purchase(description: "ER: Spot lights"),
            Purchase(description: "ER: 1 m SEÑALIZACIÓN"));

        report.Lines.Should().OnlyContain(l => l.Status == ExpenseReconLineStatus.Booked);
        report.Orphans.Should().BeEmpty();
    }

    [HumansFact]
    public void OlderExpenseReportFormat_WithoutTheMarker_Matches()
    {
        var report = Build(
            [Line(description: "ER: Cables")],
            Ledger(AliceAccount, "purchase", $"Peter D, Cables — expense report {ReportA}, 21/09/2026", credit: 100m));

        report.Lines.Single().Status.Should().Be(ExpenseReconLineStatus.Booked);
    }

    [HumansFact]
    public void IdenticalLines_AreMatchedByCount()
    {
        ExpenseHoldedReconLine[] four = [Line(), Line(), Line(), Line()];

        var extra = Build(four, Purchase(), Purchase(), Purchase(), Purchase(), Purchase());
        var short1 = Build(four, Purchase(), Purchase(), Purchase());

        extra.Lines.Count(l => l.Status == ExpenseReconLineStatus.Duplicated).Should().Be(1);
        extra.Totals.Should().Match<ExpenseReconTotals>(t => t.DuplicateLines == 1 && t.ExcessAmount == 100m && t.Orphans == 0);
        extra.Members.Single().Should().Match<ExpenseReconMember>(m =>
            m.Status == ExpenseReconMemberStatus.Off && m.UnclaimedPurchaseCount == 1);
        short1.Lines.Count(l => l.Status == ExpenseReconLineStatus.Missing).Should().Be(1);
        short1.Members.Single().MissingLines.Should().Be(1);
    }

    [HumansFact]
    public void NoLedgerLine_IsMissingWhenThePushFailed_PendingWhileQueuedOrAfterTheSync()
    {
        var failed = Line(null, state: ExpenseHoldedSyncState.Failed);
        var queued = Line(null, state: ExpenseHoldedSyncState.Queued);
        var pushedAfterSync = Line("d-late", settled: SyncedAt + Duration.FromHours(1));
        var atAnotherAmount = Line("d1", amount: 120m);

        var report = Build([failed, queued, pushedAfterSync, atAnotherAmount], Purchase(amount: 999m));

        report.Lines.Select(l => l.Status).Should().Equal(
            ExpenseReconLineStatus.Missing, ExpenseReconLineStatus.Pending, ExpenseReconLineStatus.Pending,
            ExpenseReconLineStatus.Missing);
        report.Totals.ProblemLines.Should().Be(2);
    }

    [HumansFact]
    public void MarkedPurchasesNoLineClaims_AreOrphans_ByWhetherTheirAccountIsBound()
    {
        var report = Build(
            [Line()],
            Purchase(),
            Purchase(description: "ER: Something else", account: BobAccount),
            Purchase(description: "ER: Train", account: TwinAccount),
            Purchase(description: "Office chairs", account: BobAccount));

        report.Orphans.Select(o => (o.AccountNum, o.Kind)).Should().BeEquivalentTo(
        [
            (BobAccount, ExpenseReconOrphanKind.NoMatchingLine),
            (TwinAccount, ExpenseReconOrphanKind.UnboundContact),
        ]);
        report.Totals.Problems.Should().Be(2);
    }

    [HumansFact]
    public void LegacyReportLevelDocument_MatchesOnReportIdAndAmount()
    {
        var legacy = Line(amount: 250m, description: "") with { LineId = null };

        var report = Build(
            [legacy],
            Ledger(AliceAccount, "purchase", $"Purchase, Alice, Expense report {ReportA}, 01/09/2026", credit: 250m));

        report.Lines.Single().Status.Should().Be(ExpenseReconLineStatus.Booked);
    }

    [HumansFact]
    public void BookedSepaTransfer_ClaimsItsDaysPayments_AndHandPaymentsAndALoanAreOtherMovements()
    {
        var bankDay = new LocalDate(2026, 10, 14);
        var report = Build(
            [Line(amount: 300m)],
            [
                Purchase(amount: 300m),
                Ledger(AliceAccount, "payment", "Purchase  ER: Train", debit: 200m, date: bankDay),
                Ledger(AliceAccount, "entry", "SEPA remainder", debit: 100m, date: bankDay),
                Ledger(AliceAccount, "payment", "Compra  What When Where Guide Print", debit: 40m),
                Ledger(AliceAccount, "entry", "Préstamo", credit: 53180m),
            ],
            [Transfer(AliceAccount, 300m, booked: true), Transfer(AliceAccount, 50m, booked: false)]);

        var member = report.Members.Single();
        member.Status.Should().Be(ExpenseReconMemberStatus.Balanced);
        member.IsWarning.Should().BeFalse();
        member.SepaPaid.Should().Be(300m);
        member.OtherMovements.Select(o => o.Amount).Should().BeEquivalentTo([40m, -53180m]);
        member.OpenTransfers.Should().ContainSingle().Which.Amount.Should().Be(50m);
        member.LedgerBalance.Should().Be(
            member.ExpensePurchases + member.SepaPaid + member.UnclaimedPurchases + member.OtherTotal);
    }

    [HumansFact]
    public void BookedSepaTransfer_WithNoLedgerPayment_IsAWarningNotAProblem()
    {
        var report = Build([Line()], [Purchase()], [Transfer(AliceAccount, 100m, booked: true)]);

        var member = report.Members.Single();
        member.Status.Should().Be(ExpenseReconMemberStatus.Off);
        member.UnmatchedTransfers.Should().ContainSingle();
        report.Totals.Problems.Should().Be(0);
        report.Totals.Warnings.Should().Be(1);
    }

    [HumansFact]
    public void MemberWithoutAnAccount_IsAWarningOnlyWithAMissingLine()
    {
        var pending = Build([Line(null, user: Bob, state: ExpenseHoldedSyncState.Queued)], [], [], accounts: new Dictionary<Guid, int>());
        var missing = Build([Line(user: Bob)], [], [], accounts: new Dictionary<Guid, int>());

        pending.Members.Single().Should().Match<ExpenseReconMember>(m =>
            m.Status == ExpenseReconMemberStatus.NoAccount && !m.IsWarning);
        missing.Members.Single().IsWarning.Should().BeTrue();
    }

    [HumansFact]
    public void YearFilter_NarrowsLinesAndOrphans_MembersStayAllTime()
    {
        var lastYear = Line(description: "ER: Old", report: Guid.NewGuid(), date: Instant.FromUtc(2025, 12, 31, 23, 30));
        HoldedLedgerLineInfo[] ledger =
        [
            Purchase(), Purchase(description: "ER: Old"),
            Purchase(description: "ER: Stray", date: new LocalDate(2025, 6, 1)),
        ];

        var only2026 = Build([Line(), lastYear], ledger, [], year: 2026);
        var only2025 = Build([Line(), lastYear], ledger, [], year: 2025);

        // 23:30 UTC on 31 Dec is 1 Jan in Madrid, the date the push gives the document.
        only2026.Lines.Should().HaveCount(2);
        only2026.Orphans.Should().BeEmpty();
        only2026.Years.Should().Equal(2026, 2025);
        only2025.Lines.Should().BeEmpty();
        only2025.Orphans.Should().ContainSingle();
        only2025.Members.Should().ContainSingle("members are all-time");
    }

    // --- the service: mirror reads plus the cached contact list ---

    private readonly IHoldedRepository _repo = Substitute.For<IHoldedRepository>();
    private readonly IHoldedClient _client = Substitute.For<IHoldedClient>();
    private readonly IHoldedService _holded = Substitute.For<IHoldedService>();
    private readonly IExpenseHoldedBookingService _expenses = Substitute.For<IExpenseHoldedBookingService>();
    private readonly IUserService _users = Substitute.For<IUserService>();

    private Service MakeService() => new(
        _repo, _client, Substitute.For<IBudgetService>(), _holded, Substitute.For<IHoldedWrite>(), _expenses, new FakeClock(SyncedAt),
        new MemoryCache(new MemoryCacheOptions()), Substitute.For<IAuditLogService>(), _users,
        Substitute.For<IEmailService>(), TestFinanceEmails.Create(),
        Options.Create(new SepaOptions()),
        new TrackedCache<string, Dictionary<string, int>>("Finance.SepaStuckBankLines", warmOnStartup: false, NullLogger<Service>.Instance),
        Substitute.For<IPaymentDetailsManagement>(), NullLogger<Service>.Instance);

    [HumansFact]
    public async Task GetExpenseRecon_ReadsTheAccountTheBoundContactIsOn_NotTheStoredNumber()
    {
        const int stored = 41000066;
        const int contacts = 41000065;
        _holded.GetLedgerSyncedAtAsync(Arg.Any<CancellationToken>()).Returns(SyncedAt);
        _expenses.GetHoldedReconLinesAsync(Arg.Any<CancellationToken>()).Returns([Line()]);
        _repo.GetCreditorContactsAsync(Arg.Any<CancellationToken>()).Returns(
            [new HoldedCreditorContact { UserId = Alice, HoldedContactId = "c-alice", SupplierAccountNum = stored }]);
        _client.ListContactsAsync(Arg.Any<CancellationToken>()).Returns(
            [new HoldedContactDto { Id = "c-alice", SupplierAccountNum = contacts }]);
        _repo.GetSepaPayoutTransferRowsAsync(Arg.Any<CancellationToken>()).Returns(new List<SepaPayoutTransferRow>());
        _holded.GetAccountBalancesAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, decimal> { [contacts] = -100m, [stored] = 0m });
        _holded.GetLedgerLinesAsync(contacts, Arg.Any<CancellationToken>()).Returns([Purchase(account: contacts)]);
        _holded.GetLedgerLinesAsync(stored, Arg.Any<CancellationToken>()).Returns([]);
        _users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(new Dictionary<Guid, UserInfo>()));

        var report = await MakeService().GetExpenseReconAsync(null, TestContext.Current.CancellationToken);

        report.Members.Single().SupplierAccountNum.Should().Be(contacts);
        report.Totals.Problems.Should().Be(0);
    }

    [HumansFact]
    public async Task GetExpenseRecon_LedgerNeverSynced_IsUnavailable()
    {
        _holded.GetLedgerSyncedAtAsync(Arg.Any<CancellationToken>()).Returns((Instant?)null);

        var report = await MakeService().GetExpenseReconAsync(2026, TestContext.Current.CancellationToken);

        report.IsAvailable.Should().BeFalse();
        await _expenses.DidNotReceive().GetHoldedReconLinesAsync(Arg.Any<CancellationToken>());
    }

    // --- the nightly job ---

    [HumansTheory]
    [InlineData(0, 0, LogLevel.Information)]
    [InlineData(2, 0, LogLevel.Warning)]
    [InlineData(0, 3, LogLevel.Warning)]
    public async Task Job_LogsOneSummaryLine(int orphans, int membersOff, LogLevel level)
    {
        var finance = Substitute.For<IFinanceService>();
        finance.GetExpenseReconAsync(null, Arg.Any<CancellationToken>()).Returns(new ExpenseReconReport(
            null, SyncedAt, null, [], [], [], [], new ExpenseReconTotals(1, 1, 1, 0, 0, 0m, orphans, membersOff)));
        var logger = new CapturingLogger<ExpenseReconJob>();

        await new ExpenseReconJob(finance, logger).ExecuteAsync(TestContext.Current.CancellationToken);

        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(level);
        if (orphans == 0 && membersOff == 0) entry.Message.Should().StartWith("Expense recon: 0 problems");
    }

    [HumansFact]
    public void Job_RunsAfterTheDocSyncAndTheLedgerSweep()
    {
        ((ISectionJobs)new Section()).Jobs(Substitute.For<IServiceProvider>())
            .Should().ContainSingle(j => j.JobType == typeof(ExpenseReconJob))
            .Which.Should().Be(new RecurringJobDescriptor("expense-recon", typeof(ExpenseReconJob), "30 3 * * *"));
    }
}
