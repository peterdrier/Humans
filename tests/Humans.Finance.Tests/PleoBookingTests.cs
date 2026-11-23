using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Finance.Data;
using Humans.Finance.Domain;
using Humans.Finance.Models;
using Humans.Finance.Services;
using Humans.Holded.Contracts;
using Humans.Pleo.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Finance.Tests;

internal static class TestPleo
{
    public static PleoBooking Booking() => new(
        FixedOrgTimeZone.Madrid,
        Substitute.For<IPleoService>(), TestContacts.Holded(), Substitute.For<IHoldedWrite>(),
        Substitute.For<IFinanceRepository>(), Substitute.For<IAuditLogService>(), new PleoBookingState(),
        SystemClock.Instance, NullLogger<PleoBooking>.Instance);

    public static PleoBookingRunner Runner() =>
        new(Substitute.For<IServiceScopeFactory>(), NullLogger<PleoBookingRunner>.Instance);
}

public class PleoBookingTests
{
    private const int Food = 62900001;
    private const int Geeks = 62900002;
    private const string Contact = "contact-pleo";
    private const string Treasury = "treasury-pleo";
    private static readonly DateTimeZone Madrid = DateTimeZoneProviders.Tzdb["Europe/Madrid"];
    private static readonly IReadOnlySet<int> Open = new HashSet<int> { Food, Geeks, PleoBooking.PleoTreasury };
    private static readonly HoldedChartAccountInfo[] Chart =
    [
        new(Food, "Comida", null, false, 0m),
        new(Geeks, "Geeks", null, false, 0m),
        new(PleoBooking.PleoTreasury, "Pleo", null, false, 0m),
    ];

    private readonly IPleoService _pleo = Substitute.For<IPleoService>();
    private readonly IHoldedService _holded = TestContacts.Holded();
    private readonly IHoldedWrite _write = Substitute.For<IHoldedWrite>();
    private readonly IFinanceRepository _repo = Substitute.For<IFinanceRepository>();
    private readonly PleoBookingState _state = new();
    private readonly List<HoldedLedgerLineInfo> _ledger = [];
    private readonly List<HoldedLedgerLineInfo> _expenseLines = [];
    private readonly List<HoldedPurchaseDocInfo> _docs = [];
    private readonly HashSet<string> _claims = new(StringComparer.Ordinal);
    private readonly List<PleoReceiptUpload> _uploads = [];
    private readonly List<HoldedPurchaseDocumentInput> _created = [];
    private int _docSeq;

    public PleoBookingTests()
    {
        _holded.IsConfigured.Returns(true);
        _holded.SyncAccountLedgerAsync(default, default).ReturnsForAnyArgs(true);
        _holded.SyncPurchaseDocumentsAsync(default).ReturnsForAnyArgs(true);
        _holded.SyncLedgerAsync(default, default).ReturnsForAnyArgs(true);
        _holded.GetLedgerLinesAsync(PleoBooking.PleoTreasury, Arg.Any<CancellationToken>()).Returns(_ => _ledger.ToList());
        _holded.GetLedgerLinesAsync(PleoBooking.SabadellTreasury, Arg.Any<CancellationToken>()).Returns([]);
        _holded.GetLedgerLinesAsync(Arg.Is<int>(n => n == Food || n == Geeks), Arg.Any<CancellationToken>())
            .Returns(c => _expenseLines.Where(l => l.AccountNum == c.Arg<int>()).ToList());
        _holded.GetChartAsync(Arg.Any<CancellationToken>()).Returns(Chart);
        _holded.GetPurchaseDocumentsAsync(Arg.Any<CancellationToken>())
            .Returns(_ => new HoldedPurchaseDocMirror(null, "Idle", null, 0, null, _docs.ToList()));
        _holded.GetContactsAsync(Arg.Any<CancellationToken>())
            .Returns(new HoldedContactMirror(null, [new HoldedContactDto { Id = Contact, Name = "Pleo card purchases" }]));

        _repo.GetSettingsAsync(default).ReturnsForAnyArgs(new FinanceSettings
        {
            PleoHoldedContactId = Contact,
            PleoHoldedTreasuryId = Treasury,
        });
        _repo.GetPleoBookingClaimsAsync(default).ReturnsForAnyArgs(_ => _claims.ToHashSet(StringComparer.Ordinal));
        _repo.TryClaimPleoBookingAsync(default!, default, default).ReturnsForAnyArgs(c => _claims.Add(c.Arg<string>()));
        _repo.ReleasePleoBookingClaimAsync(default!, default)
            .ReturnsForAnyArgs(c => { _claims.Remove(c.Arg<string>()); return Task.CompletedTask; });
        _repo.GetPleoReceiptUploadsAsync(default).ReturnsForAnyArgs(_ => _uploads.ToList());
        _repo.AddPleoReceiptUploadAsync(default!, default)
            .ReturnsForAnyArgs(c => { _uploads.Add(c.Arg<PleoReceiptUpload>()); return Task.CompletedTask; });

        _write.EnsureExpenseAccountAsync(default, default!, default)
            .ReturnsForAnyArgs(c => new HoldedExpenseAccountOutcome("acc-" + c.Arg<int>(), false));
        _write.CreatePurchaseDocumentAsync(default!, default).ReturnsForAnyArgs(c =>
        {
            _created.Add(c.Arg<HoldedPurchaseDocumentInput>());
            return "doc-" + ++_docSeq;
        });
        _write.PayPurchaseDocumentAsync(default!, default, default, default, default, default).ReturnsForAnyArgs("pay-1");
        _write.PostJournalEntryAsync(default, default, default, default, default!, default)
            .ReturnsForAnyArgs(new HoldedJournalEntryOutcome(true));
        _pleo.GetReceiptFileAsync(default!, default!, default)
            .ReturnsForAnyArgs(new PleoReceiptFile([1, 2, 3], "application/pdf"));
    }

    private PleoBooking Booking() => new(FixedOrgTimeZone.Madrid, _pleo, _holded, _write, _repo, Substitute.For<IAuditLogService>(), _state,
        new FakeClock(Instant.FromUtc(2026, 11, 5, 12, 0)), NullLogger<PleoBooking>.Instance);

    private void GivenEntries(params PleoEntryInfo[] entries) =>
        _pleo.GetSettledEntriesAsync(default, default, default).ReturnsForAnyArgs(entries);

    private static Instant Day(int month, int day) => new LocalDate(2026, month, day).AtStartOfDayInZone(Madrid).ToInstant();

    private static PleoEntryInfo Entry(
        string id, decimal? amount, int day = 10, string? category = "cat", string? code = "62900001",
        string family = "CARD_PURCHASE", string status = "COMPLETED", string merchant = "MAKRO", params string[] receipts) =>
        new(id, family, status, Day(6, day).Plus(Duration.FromHours(12)), amount, merchant, category, category is null ? null : code, receipts.Length > 0, null, null, receipts);

    private static HoldedLedgerLineInfo Line(int entry, int day, decimal debit, decimal credit, string? description,
        int account = PleoBooking.PleoTreasury) =>
        new(entry, 1, account, Day(6, day), "entry", description, debit, credit);

    private void GivenPurchase(string pleoId, decimal total, bool approved = true, decimal? pending = 0m) =>
        _docs.Add(new HoldedPurchaseDocInfo("doc-" + pleoId, PleoBooking.Number(pleoId), "Pleo card purchases", Contact,
            null, new LocalDate(2026, 6, 10), total, total, approved, null, [], null,
            "https://app.holded.com/doc-" + pleoId, pending));

    private PleoSnapshot Snapshot(params PleoEntryInfo[] entries) =>
        new(entries, _ledger, new HashSet<int>(), Chart, _docs, _expenseLines, _claims, _uploads);

    [HumansFact]
    public void Readiness_NeedsAmountAndAnOpenMappedAccount_ButNotAReceipt()
    {
        PleoBooking.Readiness(Entry("a", -10m), Open).Should().Be(PleoSpendState.Ready);
        PleoBooking.Readiness(Entry("a", 10m), Open).Should().Be(PleoSpendState.Ready, "a mapped refund is ready too");
        PleoBooking.Readiness(Entry("a", null), Open).Should().Be(PleoSpendState.NoAmount);
        PleoBooking.Readiness(Entry("a", -10m, category: null), Open).Should().Be(PleoSpendState.NoCategory);
        PleoBooking.Readiness(Entry("a", 10m, category: null), Open).Should().Be(PleoSpendState.NoCategory);
        PleoBooking.Readiness(Entry("a", -10m, code: "62999999"), Open).Should().Be(PleoSpendState.NotMapped);
        PleoBooking.Readiness(Entry("a", -10m, code: "57200003"), Open).Should().Be(PleoSpendState.NotMapped, "only 629 accounts");
    }

    [HumansFact]
    public void Classify_BookedByPurchaseNumberOrPleoIdOnALine_NeverByAmountAndDate()
    {
        GivenPurchase("bought", 30m);
        GivenPurchase("half", 40m, approved: true, pending: 40m);
        _ledger.Add(Line(4700, 1, 0m, 12m, "Pleo journal: MAKRO"));
        _ledger.Add(Line(4701, 2, 0m, 13m, "Purchase Pleo-paid Pleo paid: MAKRO"));
        _ledger.Add(Line(2061, 12, 0m, 71.97m, "Amazon"));

        var view = PleoBooking.Classify(Snapshot(
            Entry("bought", -30m), Entry("half", -40m), Entry("journal", -12m, day: 1),
            Entry("paid", -13m, day: 2), Entry("amazon", -71.97m, day: 12)), Madrid);

        var state = view.Spend.ToDictionary(r => r.Entry.Id, r => r.State, StringComparer.Ordinal);
        state["bought"].Should().Be(PleoSpendState.Booked);
        state["half"].Should().Be(PleoSpendState.Unfinished);
        state["journal"].Should().Be(PleoSpendState.Booked);
        state["paid"].Should().Be(PleoSpendState.Booked);
        state["amazon"].Should().Be(PleoSpendState.Ready, "a line naming no Pleo id never counts as booked");
        view.Unattributed.Should().ContainSingle().Which.EntryNumber.Should().Be(2061);
    }

    [HumansFact]
    public void Classify_ShowsARefundsLikelyOriginal_FlaggingAnotherCategory()
    {
        var view = PleoBooking.Classify(Snapshot(
            Entry("charge", -90.27m, day: 3, category: "food"),
            Entry("earlier-charge", -90.27m, day: 2, category: "food"),
            Entry("tied-charge", -90.27m, day: 3, category: "food"),
            Entry("refund", 90.27m, day: 3, category: "geeks", code: "62900002")), Madrid);

        view.RefundOrigins.Should().ContainSingle().Which.Should().Match<PleoRefundOrigin>(o =>
            o.RefundId == "refund" && o.Charge.Id == "charge" && o.CategoryDiffers);
    }

    [HumansFact]
    public async Task Book_ChargeIsAPurchasePaidFromPleo_WithEveryReceipt_OldestFirst()
    {
        GivenEntries(Entry("new", -10m, day: 20), Entry("old", -12.5m, day: 2, receipts: ["r1", "r2"]));

        var run = await Booking().RunAsync(PleoRunKind.Book, 1, null, CancellationToken.None);

        run.Lines.Should().ContainSingle().Which.Should().Match<PleoBookingLine>(l =>
            l.PleoId == "old" && l.Outcome == PleoBookingOutcome.Posted && l.Receipts == 2);
        var doc = _created.Single();
        doc.Number.Should().Be("Pleo-old");
        doc.ContactId.Should().Be(Contact);
        doc.Lines.Single().Should().Match<HoldedPurchaseDocumentLineInput>(l =>
            l.Amount == 12.5m && l.AccountId == "acc-62900001" && l.Taxes!.Count == 0 && l.Description == "MAKRO");
        await _write.Received(1).EnsurePurchaseDocumentApprovedAsync("doc-1", CancellationToken.None);
        await _write.Received(1).PayPurchaseDocumentAsync("doc-1", 12.5m, Treasury, new LocalDate(2026, 6, 2),
            Arg.Any<string?>(), CancellationToken.None);
        await _write.Received(1).UploadPurchaseAttachmentAsync("doc-1",
            Arg.Is<HoldedAttachmentInput>(a => a.FileName == "pleo-r1.pdf"), CancellationToken.None);
        await _write.Received(1).UploadPurchaseAttachmentAsync("doc-1",
            Arg.Is<HoldedAttachmentInput>(a => a.FileName == "pleo-r2.pdf"), CancellationToken.None);
        _claims.Should().Contain("old");
    }

    [HumansFact]
    public async Task Book_RefundIsANegativePurchaseOnItsOwnAccount_PaidBackIntoPleo_AndASameDayReversalNetsToZero()
    {
        GivenEntries(
            Entry("charge", -90.27m, day: 3, merchant: "DIA"),
            Entry("refund", 90.27m, day: 3, merchant: "DIA", receipts: ["abono"]));

        await Booking().RunAsync(PleoRunKind.Book, null, null, CancellationToken.None);

        _created.Select(d => d.Number).Should().Equal("Pleo-charge", "Pleo-refund");
        var refund = _created[1].Lines.Single();
        refund.Amount.Should().Be(-90.27m);
        refund.Description.Should().Be("Devolución / refund: DIA");
        _created.SelectMany(d => d.Lines).Sum(l => l.Amount).Should().Be(0m, "a same-day reversal nets to zero on its 629");
        await _write.Received(1).PayPurchaseDocumentAsync("doc-2", -90.27m, Treasury, Arg.Any<LocalDate>(),
            Arg.Any<string?>(), CancellationToken.None);
        await _write.DidNotReceive().PayPurchaseDocumentAsync("doc-1", -90.27m, Arg.Any<string?>(), Arg.Any<LocalDate>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _write.DidNotReceiveWithAnyArgs().PostJournalEntryAsync(default, default, default, default, default!, default);
    }

    [HumansFact]
    public async Task Book_SkipsWhatHoldedHas_AndFinishesAHalfDonePurchaseWithoutRecreatingIt()
    {
        GivenPurchase("done", 30m);
        GivenPurchase("half", 40m, approved: false, pending: 40m);
        _ledger.Add(Line(4700, 1, 0m, 12m, "Purchase Pleo-paid"));
        GivenEntries(Entry("done", -30m, day: 1), Entry("paid", -12m, day: 1), Entry("half", -40m, day: 2));

        var run = await Booking().RunAsync(PleoRunKind.Book, null, null, CancellationToken.None);

        run.Lines.Select(l => l.PleoId).Should().Equal("half");
        _created.Should().BeEmpty();
        await _write.Received(1).EnsurePurchaseDocumentApprovedAsync("doc-half", CancellationToken.None);
        await _write.Received(1).PayPurchaseDocumentAsync("doc-half", 40m, Treasury, Arg.Any<LocalDate>(),
            Arg.Any<string?>(), CancellationToken.None);
    }

    [HumansTheory]
    [InlineData(-40, null)]
    [InlineData(-40, 0)]
    [InlineData(25, null)]
    [InlineData(25, 0)]
    public async Task Book_ADraftLeftByAFailedApproval_IsApprovedAndPaidInFull(int amount, int? mirrorPending)
    {
        // Holded omits a draft's balance; the mirror must not read that as already paid.
        GivenPurchase("draft", -amount, approved: false, pending: mirrorPending);
        GivenEntries(Entry("draft", amount));

        var run = await Booking().RunAsync(PleoRunKind.Book, null, null, CancellationToken.None);

        run.Lines.Should().ContainSingle().Which.Outcome.Should().Be(PleoBookingOutcome.Posted);
        _created.Should().BeEmpty();
        await _write.Received(1).EnsurePurchaseDocumentApprovedAsync("doc-draft", CancellationToken.None);
        await _write.Received(1).PayPurchaseDocumentAsync("doc-draft", -amount, Treasury, Arg.Any<LocalDate>(),
            Arg.Any<string?>(), CancellationToken.None);
    }

    [HumansFact]
    public async Task Book_AClaimedEntryHoldedDoesNotList_IsNeverCreatedAgain()
    {
        _claims.Add("lost");
        GivenEntries(Entry("lost", -10m));

        var run = await Booking().RunAsync(PleoRunKind.Book, null, null, CancellationToken.None);

        run.Lines.Should().BeEmpty();
        _created.Should().BeEmpty();
        PleoBooking.Classify(Snapshot(Entry("lost", -10m)), Madrid).Spend.Single().State.Should().Be(PleoSpendState.CheckInHolded);
    }

    [HumansFact]
    public async Task Book_RefusedCreateReleasesTheClaim_AnUnreadableOrTimedOutCreateKeepsIt()
    {
        GivenEntries(Entry("a", -10m, day: 1), Entry("b", -11m, day: 2), Entry("c", -12m, day: 3), Entry("d", -13m, day: 4));
        _write.CreatePurchaseDocumentAsync(default!, default).ReturnsForAnyArgs(
            Task.FromException<string>(new HoldedPermanentException(400, null, "400")),
            Task.FromException<string>(new HoldedPermanentException("created, no id in the answer")),
            Task.FromException<string>(new HoldedTransientException("timeout")));

        var run = await Booking().RunAsync(PleoRunKind.Book, null, null, CancellationToken.None);

        run.Lines.Select(l => (l.PleoId, l.Outcome)).Should().Equal(
            ("a", PleoBookingOutcome.Failed), ("b", PleoBookingOutcome.Failed), ("c", PleoBookingOutcome.Failed));
        run.Refused.Should().Contain("Stopped");
        _claims.Should().BeEquivalentTo(["b", "c"], "only a 4xx proves nothing was created");
    }

    [HumansFact]
    public async Task Book_SecondConcurrentRunIsRefused()
    {
        GivenEntries(Entry("a", -10m));
        using var release = new SemaphoreSlim(0);
        _write.CreatePurchaseDocumentAsync(default!, default)
            .ReturnsForAnyArgs(async _ => { await release.WaitAsync(); return "doc-1"; });

        var first = Booking().RunAsync(PleoRunKind.Book, 1, null, CancellationToken.None);
        var second = await Booking().RunAsync(PleoRunKind.Corrections, 1, null, CancellationToken.None);
        release.Release();
        await first;

        second.Refused.Should().Contain("Another Pleo run");
        await _write.ReceivedWithAnyArgs(1).CreatePurchaseDocumentAsync(default!, default);
    }

    [HumansFact]
    public async Task Book_RefusedWithoutTheContactAndTreasurySettings()
    {
        _repo.GetSettingsAsync(default).ReturnsForAnyArgs(new FinanceSettings());
        GivenEntries(Entry("a", -10m));

        var run = await Booking().RunAsync(PleoRunKind.Book, null, null, CancellationToken.None);

        run.Refused.Should().Contain("/Settings");
        _created.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Receipts_LateReceiptIsAttachedOnce_OnlyWhatIsMissing()
    {
        GivenPurchase("a", 10m);
        _uploads.Add(new PleoReceiptUpload { PleoEntryId = "a", ReceiptId = "r1", Confirmed = true });
        GivenEntries(Entry("a", -10m, receipts: ["r1", "r2"]));

        var run = await Booking().RunAsync(PleoRunKind.Receipts, null, null, CancellationToken.None);
        await Booking().RunAsync(PleoRunKind.Receipts, null, null, CancellationToken.None);

        run.Lines.Single().Receipts.Should().Be(1);
        await _write.Received(1).UploadPurchaseAttachmentAsync("doc-a",
            Arg.Is<HoldedAttachmentInput>(a => a.FileName == "pleo-r2.pdf"), CancellationToken.None);
        await _write.ReceivedWithAnyArgs(1).UploadPurchaseAttachmentAsync(default!, default!, default);
    }

    [HumansTheory]
    [InlineData(1)]
    [InlineData(null)]
    public async Task Corrections_SelectOldestSettledEntriesBeforeApplyingTheLimit(int? limit)
    {
        GivenPurchase("a-new", 20m);
        GivenPurchase("z-old", 20m);
        GivenPurchase("b-old", 20m);
        _expenseLines.Add(Line(10, 1, 20m, 0m, "Purchase Pleo-a-new", Food));
        _expenseLines.Add(Line(11, 1, 20m, 0m, "Purchase Pleo-z-old", Food));
        _expenseLines.Add(Line(12, 1, 20m, 0m, "Purchase Pleo-b-old", Food));
        GivenEntries(Entry("a-new", -25m, day: 20), Entry("z-old", -25m, day: 2), Entry("b-old", -25m, day: 2));

        var run = await Booking().RunAsync(PleoRunKind.Corrections, limit, null, CancellationToken.None);

        var expected = new[] { "b-old", "z-old", "a-new" }.Take(limit ?? int.MaxValue);
        run.Lines.Select(l => l.PleoId).Should().Equal(expected);
        run.Lines.Should().OnlyContain(l => l.Outcome == PleoBookingOutcome.Posted);
    }

    [HumansFact]
    public async Task Corrections_RecategorisedAndRepricedEachPostOne_AndNoneOncePosted()
    {
        GivenPurchase("moved", 50m);
        GivenPurchase("repriced", 20m);
        _expenseLines.Add(Line(10, 1, 50m, 0m, "Purchase Pleo-moved", Food));
        _expenseLines.Add(Line(11, 2, 20m, 0m, "Purchase Pleo-repriced", Food));
        GivenEntries(Entry("moved", -50m, day: 1, code: "62900002"), Entry("repriced", -25m, day: 2));

        var run = await Booking().RunAsync(PleoRunKind.Corrections, null, null, CancellationToken.None);

        run.Lines.Should().HaveCount(2);
        await _write.Received(1).PostJournalEntryAsync(new LocalDate(2026, 11, 5), Geeks, Food, 50m,
            "Pleo moved: recategorizado / recategorised 62900001 Comida → 62900002 Geeks", CancellationToken.None);
        await _write.Received(1).PostJournalEntryAsync(new LocalDate(2026, 11, 5), Food, PleoBooking.PleoTreasury, 5m,
            "Pleo repriced: importe cambiado / amount changed 20.00 → 25.00", CancellationToken.None);

        _expenseLines.Add(Line(20, 5, 50m, 0m, "Pleo moved: recategorizado / recategorised", Geeks));
        _expenseLines.Add(Line(20, 5, 0m, 50m, "Pleo moved: recategorizado / recategorised", Food));
        _expenseLines.Add(Line(21, 5, 5m, 0m, "Pleo repriced: importe cambiado / amount changed", Food));
        var again = await Booking().RunAsync(PleoRunKind.Corrections, null, null, CancellationToken.None);

        again.Lines.Should().BeEmpty();
        await _write.ReceivedWithAnyArgs(2).PostJournalEntryAsync(default, default, default, default, default!, default);
    }

    [HumansFact]
    public void Corrections_ARefundMovedCreditsTheNewAccount_AndAnEntryGoneFromPleoIsListedOnly()
    {
        GivenPurchase("refund", -30m);
        GivenPurchase("gone", 15m);
        _expenseLines.Add(Line(10, 1, -30m, 0m, "Purchase Pleo-refund", Food));
        _expenseLines.Add(Line(11, 1, 15m, 0m, "Purchase Pleo-gone", Food));

        var view = PleoBooking.Classify(Snapshot(Entry("refund", 30m, code: "62900002")), Madrid);

        view.Changes.Single(c => string.Equals(c.PleoId, "refund", StringComparison.Ordinal)).Corrections.Should().ContainSingle().Which
            .Should().Match<PleoCorrection>(c => c.Debit == Food && c.Credit == Geeks && c.Amount == 30m);
        view.Changes.Single(c => string.Equals(c.PleoId, "gone", StringComparison.Ordinal)).Corrections.Should().BeEmpty();
    }

    [HumansFact]
    public void Corrections_AnEntryCorrectedToZeroIsStillCompared()
    {
        GivenPurchase("zeroed", 100m);
        _expenseLines.Add(Line(10, 1, 100m, 0m, "Purchase Pleo-zeroed", Food));
        _expenseLines.Add(Line(11, 2, 0m, 100m, "Pleo zeroed: importe cambiado / amount changed", Food));

        var view = PleoBooking.Classify(Snapshot(Entry("zeroed", -20m)), Madrid);

        view.Changes.Single().Corrections.Should().ContainSingle().Which.Should().Match<PleoCorrection>(c =>
            c.Debit == Food && c.Credit == PleoBooking.PleoTreasury && c.Amount == 20m);
    }
}
