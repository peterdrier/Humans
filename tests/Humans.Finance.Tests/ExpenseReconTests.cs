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

/// <summary>/Finance/ExpenseRecon: approved lines against the seeded document and ledger mirrors.</summary>
public class ExpenseReconTests
{
    private static readonly Instant SyncedAt = Instant.FromUtc(2026, 10, 29, 2, 45);
    private static readonly Instant Submitted = Instant.FromUtc(2026, 10, 10, 9, 0);
    private static readonly Guid Alice = Guid.NewGuid();
    private static readonly Guid Bob = Guid.NewGuid();
    private static readonly Guid ReportA = Guid.NewGuid();
    private const int AliceAccount = 41000001;
    private const int BobAccount = 41000002;

    private static ExpenseHoldedReconLine Line(
        string? docId, decimal amount = 100m, string description = "ER: Train", Guid? user = null, Guid? report = null,
        ExpenseHoldedSyncState state = ExpenseHoldedSyncState.Pushed, Instant? settled = null,
        Instant? date = null, string contact = "c-alice") =>
        new(report ?? ReportA, Guid.NewGuid(), ExpenseReportStatus.Approved, user ?? Alice, contact, docId, description,
            $"Expense report {report ?? ReportA}", amount, date ?? Submitted, state, settled ?? Submitted);

    private static HoldedExpenseDoc Doc(
        string id, decimal total = 100m, string? description = "ER: Train", string? contactId = "c-alice",
        string contactName = "Alice Example", string? notes = null, Guid? report = null, LocalDate? date = null) => new()
        {
            Id = Guid.NewGuid(),
            HoldedDocId = id,
            DocNumber = id.ToUpperInvariant(),
            ContactId = contactId,
            ContactName = contactName,
            Description = description,
            Notes = notes ?? $"Expense report {report ?? ReportA}",
            Date = date ?? new LocalDate(2026, 10, 10),
            Total = total,
        };

    private static HoldedCreditorContact Binding(Guid user, string contact, int? account) => new()
    {
        UserId = user,
        HoldedContactId = contact,
        SupplierAccountNum = account,
        Source = CreditorContactSource.Auto,
    };

    private static readonly HoldedCreditorContact[] Bindings =
        [Binding(Alice, "c-alice", AliceAccount), Binding(Bob, "c-bob", BobAccount)];

    private static SepaPayoutTransferRow Transfer(int account, decimal amount, bool booked, bool withdrawn = false) => new(
        Guid.NewGuid(), Guid.NewGuid(), "payout-1.xml", Submitted, Guid.NewGuid(), Alice, account, "c-alice", "ALICE",
        "ES** **** 1234", amount, booked ? Submitted : null, null, null, null, null, null, null,
        WithdrawnAt: withdrawn ? Submitted : null);

    private static ExpenseReconReport Build(
        IReadOnlyList<ExpenseHoldedReconLine> lines, IReadOnlyList<HoldedExpenseDoc> docs,
        IReadOnlyDictionary<int, decimal>? balances = null, IReadOnlyList<SepaPayoutTransferRow>? transfers = null,
        IReadOnlyList<HoldedCreditorContact>? bindings = null, int? year = null) =>
        ExpenseRecon.Build(year, SyncedAt, lines, docs, bindings ?? Bindings,
            balances ?? new Dictionary<int, decimal>(), transfers ?? [], id => id == Alice ? "Alice" : "Bob");

    [HumansFact]
    public void OneDocPerLine_OnTheBoundContact_BalancedLedger_IsZeroProblems()
    {
        var report = Build([Line("d1")], [Doc("d1")], new Dictionary<int, decimal> { [AliceAccount] = -100m });

        report.Lines.Should().ContainSingle().Which.Status.Should().Be(ExpenseReconLineStatus.Booked);
        report.Members.Should().ContainSingle().Which.Status.Should().Be(ExpenseReconMemberStatus.Balanced);
        report.Totals.Problems.Should().Be(0);
        report.Totals.Should().Be(new ExpenseReconTotals(1, 1, 1, 0, 0, 0m, 0, 0));
    }

    [HumansFact]
    public void NoDocument_IsMissingWhenThePushFailed_PendingWhileQueued()
    {
        var failed = Line(null, state: ExpenseHoldedSyncState.Failed, report: Guid.NewGuid());
        var queued = Line(null, state: ExpenseHoldedSyncState.Queued, report: Guid.NewGuid());
        var pushedAfterSync = Line("d-late", report: Guid.NewGuid(), settled: SyncedAt + Duration.FromHours(1));
        var pushedButGone = Line("d-gone", report: Guid.NewGuid());

        var report = Build([failed, queued, pushedAfterSync, pushedButGone], []);

        var byId = report.Lines.ToDictionary(l => l.LineId!.Value);
        byId[failed.LineId!.Value].Should().Match<ExpenseReconLine>(l =>
            l.Status == ExpenseReconLineStatus.Missing && l.PushState == "Failed");
        byId[queued.LineId!.Value].Status.Should().Be(ExpenseReconLineStatus.Pending);
        byId[pushedAfterSync.LineId!.Value].Status.Should().Be(ExpenseReconLineStatus.Pending);
        byId[pushedButGone.LineId!.Value].Status.Should().Be(ExpenseReconLineStatus.Missing);
        report.Totals.ProblemLines.Should().Be(2);
    }

    [HumansFact]
    public void RacedDuplicates_AreCountedWithTheirExcess_AndPutTheMemberOff()
    {
        var report = Build(
            [Line("d1")],
            [Doc("d1"), Doc("d2"), Doc("d3")],
            new Dictionary<int, decimal> { [AliceAccount] = -300m });

        var line = report.Lines.Should().ContainSingle().Subject;
        line.Status.Should().Be(ExpenseReconLineStatus.Duplicated);
        line.DuplicateDocIds.Should().BeEquivalentTo(["d2", "d3"]);
        line.ExcessAmount.Should().Be(200m);
        report.Totals.DuplicateDocs.Should().Be(2);
        report.Totals.ExcessAmount.Should().Be(200m);
        report.Totals.HoldedDocs.Should().Be(3);
        var member = report.Members.Should().ContainSingle().Subject;
        member.Status.Should().Be(ExpenseReconMemberStatus.Off);
        member.Difference.Should().Be(-200m);
    }

    [HumansFact]
    public void SameDescriptionAndTotal_WithAnotherReportsNotes_IsNotADuplicate_ButAnOrphan()
    {
        var other = Guid.NewGuid();
        var report = Build([Line("d1")], [Doc("d1"), Doc("d2", report: other)]);

        report.Lines.Single().Status.Should().Be(ExpenseReconLineStatus.Booked);
        report.Orphans.Should().ContainSingle().Which.Should().Match<ExpenseReconOrphan>(o =>
            o.HoldedDocId == "d2" && o.Kind == ExpenseReconOrphanKind.NoMatchingLine);
    }

    [HumansFact]
    public void UnfetchedNotes_FallBackToTheListLevelRule()
    {
        var unfetched = Doc("d2");
        unfetched.Notes = null;

        var report = Build([Line("d1")], [Doc("d1"), unfetched]);

        report.Lines.Single().Status.Should().Be(ExpenseReconLineStatus.Duplicated);
    }

    [HumansFact]
    public void DuplicateOnASameNamedTwinContact_IsFound()
    {
        var report = Build([Line("d1")], [Doc("d1"), Doc("d2", contactId: "c-twin", contactName: " alice example ")]);

        report.Lines.Single().DuplicateDocIds.Should().Equal("d2");
    }

    [HumansFact]
    public void DocumentAtAnotherAmount_IsAmountMismatch()
    {
        var report = Build([Line("d1")], [Doc("d1", total: 120m)], new Dictionary<int, decimal> { [AliceAccount] = -120m });

        report.Lines.Single().Should().Match<ExpenseReconLine>(l =>
            l.Status == ExpenseReconLineStatus.AmountMismatch && l.HoldedTotal == 120m);
        report.Members.Single().Status.Should().Be(ExpenseReconMemberStatus.Off);
    }

    [HumansFact]
    public void DocumentOnATwinContact_IsWrongContact()
    {
        var report = Build([Line("d1")], [Doc("d1", contactId: "c-twin")]);

        report.Lines.Single().Status.Should().Be(ExpenseReconLineStatus.WrongContact);
    }

    [HumansFact]
    public void MarkedDocumentsNoLineClaims_AreOrphans_ByWhetherTheirContactIsBound()
    {
        var report = Build(
            [Line("d1")],
            [
                Doc("d1"),
                Doc("o-bound", description: "Something else", contactId: "c-bob", report: Guid.NewGuid()),
                Doc("o-unbound", notes: "", contactId: "c-nobody", contactName: "Nobody"),
                Doc("vendor", description: "Office chairs", notes: "", contactId: "c-vendor", contactName: "Vendor"),
            ],
            new Dictionary<int, decimal> { [AliceAccount] = -100m });

        report.Orphans.Select(o => (o.HoldedDocId, o.Kind)).Should().BeEquivalentTo(
        [
            ("o-bound", ExpenseReconOrphanKind.NoMatchingLine),
            ("o-unbound", ExpenseReconOrphanKind.UnboundContact),
        ]);
        report.Totals.Orphans.Should().Be(2);
        report.Totals.Problems.Should().Be(2);
    }

    [HumansFact]
    public void BookedSepaTransfer_SettlesTheExpectedBalance()
    {
        var report = Build(
            [Line("d1")], [Doc("d1")],
            new Dictionary<int, decimal> { [AliceAccount] = 0m },
            [Transfer(AliceAccount, 100m, booked: true), Transfer(AliceAccount, 50m, booked: false, withdrawn: true)]);

        var member = report.Members.Single();
        member.Status.Should().Be(ExpenseReconMemberStatus.Balanced);
        member.OpenTransfers.Should().BeEmpty();
    }

    [HumansFact]
    public void PaidInHoldedButTransferNotBooked_IsOff_WithTheOpenTransferListed()
    {
        var report = Build(
            [Line("d1")], [Doc("d1")],
            new Dictionary<int, decimal> { [AliceAccount] = 0m },
            [Transfer(AliceAccount, 100m, booked: false)]);

        var member = report.Members.Single();
        member.Status.Should().Be(ExpenseReconMemberStatus.Off);
        member.Difference.Should().Be(100m);
        member.OpenTransfers.Should().ContainSingle().Which.Amount.Should().Be(100m);
    }

    [HumansFact]
    public void MemberOff_IsAWarningNotAProblem()
    {
        var report = Build(
            [Line("d1")], [Doc("d1")],
            new Dictionary<int, decimal> { [AliceAccount] = 0m },
            [Transfer(AliceAccount, 100m, booked: false)]);

        report.Members.Single().IsWarning.Should().BeTrue();
        report.Totals.Problems.Should().Be(0);
        report.Totals.Warnings.Should().Be(1);
    }

    [HumansFact]
    public void Problems_CountLinesAndOrphans_WarningsCountMembers()
    {
        var totals = new ExpenseReconTotals(1, 1, 1, ProblemLines: 2, DuplicateDocs: 0, ExcessAmount: 0m, Orphans: 3, MembersOff: 4);

        totals.Problems.Should().Be(5);
        totals.Warnings.Should().Be(4);
    }

    [HumansFact]
    public void GeneratedNotPaid_StaysBalanced_AndListsTheOpenTransfer()
    {
        var report = Build(
            [Line("d1")], [Doc("d1")],
            new Dictionary<int, decimal> { [AliceAccount] = -100m },
            [Transfer(AliceAccount, 100m, booked: false)]);

        var member = report.Members.Single();
        member.Status.Should().Be(ExpenseReconMemberStatus.Balanced);
        member.OpenTransfers.Should().ContainSingle();
    }

    [HumansFact]
    public void MemberWithoutAnAccount_IsAWarningOnlyOnceHoldedHoldsTheirDocument()
    {
        var pending = Build([Line(null, user: Bob, state: ExpenseHoldedSyncState.Queued)], [], bindings: []);
        var booked = Build([Line("d1", user: Bob, contact: "c-bob")], [Doc("d1", contactId: "c-bob")], bindings: []);

        pending.Members.Single().Should().Match<ExpenseReconMember>(m =>
            m.Status == ExpenseReconMemberStatus.NoAccount && !m.IsWarning);
        booked.Members.Single().IsWarning.Should().BeTrue();
    }

    [HumansFact]
    public void YearFilter_NarrowsLinesAndOrphans_MembersStayAllTime()
    {
        var lastYear = Line("d-old", report: Guid.NewGuid(), date: Instant.FromUtc(2025, 12, 31, 23, 30));
        var report = Build(
            [Line("d1"), lastYear],
            [
                Doc("d1"), Doc("d-old", report: lastYear.ExpenseReportId, date: new LocalDate(2026, 1, 1)),
                Doc("orphan-2025", notes: "Expense report x", description: null, date: new LocalDate(2025, 6, 1)),
            ],
            new Dictionary<int, decimal> { [AliceAccount] = -200m },
            year: 2026);

        // 23:30 UTC on 31 Dec is 1 Jan in Madrid, the date the push gives the document.
        report.Lines.Should().HaveCount(2);
        report.Orphans.Should().BeEmpty();
        report.Members.Single().Status.Should().Be(ExpenseReconMemberStatus.Balanced);
        report.Years.Should().Equal(2026, 2025);

        var only2025 = Build([Line("d1")], [Doc("d1"), Doc("orphan-2025", notes: "Expense report x",
            description: null, date: new LocalDate(2025, 6, 1))], year: 2025);
        only2025.Lines.Should().BeEmpty();
        only2025.Orphans.Should().ContainSingle();
        only2025.Members.Should().ContainSingle("members are all-time");
    }

    [HumansFact]
    public void LegacyReportLevelDocument_MatchesById_AndCountsTowardTheBalance()
    {
        var legacy = Line("d-legacy", amount: 250m, description: "") with { LineId = null };

        var report = Build([legacy], [Doc("d-legacy", total: 250m, description: "Legacy")],
            new Dictionary<int, decimal> { [AliceAccount] = -250m });

        report.Lines.Single().Status.Should().Be(ExpenseReconLineStatus.Booked);
        report.Orphans.Should().BeEmpty();
        report.Members.Single().Status.Should().Be(ExpenseReconMemberStatus.Balanced);
    }

    // --- the service: local reads only ---

    private readonly IHoldedRepository _repo = Substitute.For<IHoldedRepository>();
    private readonly IHoldedClient _client = Substitute.For<IHoldedClient>();
    private readonly IHoldedService _holded = Substitute.For<IHoldedService>();
    private readonly IExpenseHoldedBookingService _expenses = Substitute.For<IExpenseHoldedBookingService>();
    private readonly IUserService _users = Substitute.For<IUserService>();

    private Service MakeService() => new(
        _repo, _client, Substitute.For<IBudgetService>(), _holded, _expenses, new FakeClock(SyncedAt),
        new MemoryCache(new MemoryCacheOptions()), Substitute.For<IAuditLogService>(), _users,
        Substitute.For<IUserEmailService>(), Substitute.For<IEmailService>(), TestFinanceEmails.Create(),
        Options.Create(new SepaOptions()),
        new TrackedCache<string, Dictionary<string, int>>("Finance.SepaStuckBankLines", warmOnStartup: false, NullLogger<Service>.Instance),
        NullLogger<Service>.Instance);

    [HumansFact]
    public async Task GetExpenseRecon_ReadsTheMirrorsOnly_NeverHolded()
    {
        _repo.GetOrCreateDocSyncStateAsync(Arg.Any<CancellationToken>())
            .Returns(new HoldedDocSyncState { LastSyncAt = SyncedAt });
        _expenses.GetHoldedReconLinesAsync(Arg.Any<CancellationToken>()).Returns([Line("d1")]);
        _repo.GetAllDocsAsync(Arg.Any<CancellationToken>()).Returns([Doc("d1")]);
        _repo.GetCreditorContactsAsync(Arg.Any<CancellationToken>()).Returns(Bindings);
        _repo.GetSepaPayoutTransferRowsAsync(Arg.Any<CancellationToken>()).Returns(new List<SepaPayoutTransferRow>());
        _holded.GetAccountBalancesAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, decimal> { [AliceAccount] = -100m });
        _users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(new Dictionary<Guid, UserInfo>()));

        var report = await MakeService().GetExpenseReconAsync(null, TestContext.Current.CancellationToken);

        report.IsAvailable.Should().BeTrue();
        report.Totals.Problems.Should().Be(0);
        _client.ReceivedCalls().Should().BeEmpty();
    }

    [HumansFact]
    public async Task GetExpenseRecon_MirrorNeverSynced_IsUnavailable()
    {
        _repo.GetOrCreateDocSyncStateAsync(Arg.Any<CancellationToken>()).Returns(new HoldedDocSyncState());

        var report = await MakeService().GetExpenseReconAsync(2026, TestContext.Current.CancellationToken);

        report.IsAvailable.Should().BeFalse();
        await _expenses.DidNotReceive().GetHoldedReconLinesAsync(Arg.Any<CancellationToken>());
    }

    // --- notes captured at sync ---

    private static HoldedPurchaseDocListItemDto ListDoc(string id, string description = "ER: Train") => new()
    {
        Id = id,
        DocNumber = id,
        ContactId = "c-alice",
        ContactName = "Alice Example",
        Description = description,
        Date = Submitted,
        Subtotal = 100m,
        Tax = 0m,
        Total = 100m,
    };

    private static HoldedPurchaseDocumentDto FullDoc(string id, string? notes) => new()
    {
        Notes = notes,
    };

    private void StubSync(IReadOnlyList<HoldedPurchaseDocListItemDto> listed, IReadOnlyList<HoldedExpenseDoc> mirrored)
    {
        _repo.GetOrCreateDocSyncStateAsync(Arg.Any<CancellationToken>()).Returns(new HoldedDocSyncState());
        _repo.GetCategoryMapAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedCategoryMap>());
        _repo.GetManagedAccountsAsync(Arg.Any<CancellationToken>()).Returns(new List<HoldedManagedAccount>());
        _expenses.GetHoldedBookingsAsync(Arg.Any<CancellationToken>()).Returns(new List<ExpenseHoldedBooking>());
        _client.ListPurchaseDocumentsAsync(Arg.Any<CancellationToken>()).Returns(listed);
        _repo.GetAllDocsAsync(Arg.Any<CancellationToken>()).Returns(mirrored);
    }

    [HumansFact]
    public async Task Sync_FetchesNotesOncePerNewDocument_AndLeavesKnownOnesToTheUpsert()
    {
        StubSync([ListDoc("new"), ListDoc("known")], [Doc("known", notes: "Expense report k")]);
        _client.GetPurchaseDocumentAsync("new", Arg.Any<CancellationToken>()).Returns(FullDoc("new", "Expense report n"));
        IReadOnlyList<HoldedExpenseDoc>? written = null;
        await _repo.ReplaceDocsAsync(Arg.Do<IReadOnlyList<HoldedExpenseDoc>>(d => written = d),
            Arg.Any<Instant>(), Arg.Any<CancellationToken>());

        await MakeService().SyncAsync(TestContext.Current.CancellationToken);

        await _client.Received(1).GetPurchaseDocumentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        Written(written, "new").Notes.Should().Be("Expense report n");
        Written(written, "new").ContactId.Should().Be("c-alice");
        Written(written, "known").Notes.Should().BeNull("the upsert keeps the stored notes");
    }

    [HumansFact]
    public async Task Sync_TransientNotesFailure_StopsFetching_AndTheSyncSucceeds()
    {
        StubSync([ListDoc("a"), ListDoc("b")], []);
        _client.GetPurchaseDocumentAsync("a", Arg.Any<CancellationToken>())
            .ThrowsAsync(new HoldedTransientException("timeout"));
        IReadOnlyList<HoldedExpenseDoc>? written = null;
        await _repo.ReplaceDocsAsync(Arg.Do<IReadOnlyList<HoldedExpenseDoc>>(d => written = d),
            Arg.Any<Instant>(), Arg.Any<CancellationToken>());

        var result = await MakeService().SyncAsync(TestContext.Current.CancellationToken);

        result.DocCount.Should().Be(2);
        Written(written, "a").Notes.Should().BeNull();
        Written(written, "b").Notes.Should().BeNull("the run stopped at the transient failure");
        await _client.Received(1).GetPurchaseDocumentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Sync_FetchesNotesOnlyForPushWrittenDocuments()
    {
        StubSync([ListDoc("er"), ListDoc("vendor", "Office chairs")], []);
        _client.GetPurchaseDocumentAsync("er", Arg.Any<CancellationToken>()).Returns(FullDoc("er", "Expense report e"));
        IReadOnlyList<HoldedExpenseDoc>? written = null;
        await _repo.ReplaceDocsAsync(Arg.Do<IReadOnlyList<HoldedExpenseDoc>>(d => written = d),
            Arg.Any<Instant>(), Arg.Any<CancellationToken>());

        await MakeService().SyncAsync(TestContext.Current.CancellationToken);

        await _client.Received(1).GetPurchaseDocumentAsync("er", Arg.Any<CancellationToken>());
        await _client.DidNotReceive().GetPurchaseDocumentAsync("vendor", Arg.Any<CancellationToken>());
        Written(written, "vendor").Notes.Should().BeNull();
    }

    [HumansFact]
    public async Task Sync_CapsNotesFetchesPerRun()
    {
        StubSync(Enumerable.Range(0, 80).Select(i => ListDoc($"d{i}")).ToList(), []);
        _client.GetPurchaseDocumentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => FullDoc(ci.Arg<string>(), "Expense report x"));

        await MakeService().SyncAsync(TestContext.Current.CancellationToken);

        await _client.Received(50).GetPurchaseDocumentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetExpenseRecon_LedgerMirrorEmpty_IsUnavailable()
    {
        _repo.GetOrCreateDocSyncStateAsync(Arg.Any<CancellationToken>())
            .Returns(new HoldedDocSyncState { LastSyncAt = SyncedAt });
        _expenses.GetHoldedReconLinesAsync(Arg.Any<CancellationToken>()).Returns([Line("d1")]);
        _repo.GetAllDocsAsync(Arg.Any<CancellationToken>()).Returns([Doc("d1")]);
        _repo.GetCreditorContactsAsync(Arg.Any<CancellationToken>()).Returns(Bindings);
        _holded.GetAccountBalancesAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<int, decimal>());

        var report = await MakeService().GetExpenseReconAsync(null, TestContext.Current.CancellationToken);

        report.IsAvailable.Should().BeFalse();
    }

    [HumansFact]
    public void Build_LinkedDocWithoutContact_IsWrongContact()
    {
        var report = Build([Line("d1")], [Doc("d1", contactId: null)]);

        report.Lines.Single().Status.Should().Be(ExpenseReconLineStatus.WrongContact);
    }

    private static HoldedExpenseDoc Written(IReadOnlyList<HoldedExpenseDoc>? written, string id) =>
        written!.Single(d => string.Equals(d.HoldedDocId, id, StringComparison.Ordinal));

    // --- the nightly job ---

    [HumansTheory]
    [InlineData(0, 0, LogLevel.Information)]
    [InlineData(2, 0, LogLevel.Warning)]
    [InlineData(0, 3, LogLevel.Warning)]
    public async Task Job_LogsOneSummaryLine(int orphans, int membersOff, LogLevel level)
    {
        var finance = Substitute.For<IHoldedFinanceService>();
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
