using AwesomeAssertions;
using Humans.AuditLog.Contracts;
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
        Substitute.For<IPleoService>(), TestContacts.Holded(), Substitute.For<IHoldedWrite>(),
        Substitute.For<IAuditLogService>(), new PleoBookingState(), SystemClock.Instance,
        NullLogger<PleoBooking>.Instance);

    public static PleoBookingRunner Runner() =>
        new(Substitute.For<IServiceScopeFactory>(), NullLogger<PleoBookingRunner>.Instance);
}

public class PleoBookingTests
{
    private const int Food = 62900001;
    private static readonly DateTimeZone Madrid = DateTimeZoneProviders.Tzdb["Europe/Madrid"];
    private static readonly IReadOnlySet<int> Open = new HashSet<int> { Food, PleoBooking.PleoTreasury };

    private readonly IPleoService _pleo = Substitute.For<IPleoService>();
    private readonly IHoldedService _holded = TestContacts.Holded();
    private readonly IHoldedWrite _write = Substitute.For<IHoldedWrite>();
    private readonly PleoBookingState _state = new();
    private readonly List<HoldedLedgerLineInfo> _ledger = [];

    public PleoBookingTests()
    {
        _holded.IsConfigured.Returns(true);
        _holded.SyncAccountLedgerAsync(PleoBooking.PleoTreasury, Arg.Any<CancellationToken>()).Returns(true);
        _holded.GetLedgerLinesAsync(PleoBooking.PleoTreasury, Arg.Any<CancellationToken>()).Returns(_ => _ledger.ToList());
        _holded.GetLedgerLinesAsync(PleoBooking.SabadellTreasury, Arg.Any<CancellationToken>()).Returns([]);
        _holded.GetChartAsync(Arg.Any<CancellationToken>())
            .Returns([new HoldedChartAccountInfo(Food, "Comida", null, null, false, 0m)]);
        _write.PostJournalEntryAsync(default, default, default, default, default!, default)
            .ReturnsForAnyArgs(new HoldedJournalEntryOutcome(true));

    }

    private PleoBooking Booking() => new(_pleo, _holded, _write, Substitute.For<IAuditLogService>(), _state,
        new FakeClock(Instant.FromUtc(2026, 11, 5, 12, 0)), NullLogger<PleoBooking>.Instance);

    private void GivenEntries(params PleoEntryInfo[] entries) =>
        _pleo.GetSettledEntriesAsync(default, default, default).ReturnsForAnyArgs(entries);

    private static Instant Day(int month, int day) => new LocalDate(2026, month, day).AtStartOfDayInZone(Madrid).ToInstant();

    private static PleoEntryInfo Entry(
        string id, decimal? amount, int day = 10, bool receipt = true, string? category = "cat",
        string? code = "62900001", string family = "CARD_PURCHASE", string status = "COMPLETED") =>
        new(id, family, null, status, Day(6, day).Plus(Duration.FromHours(12)), "EUR", 0, amount, "MAKRO",
            null, category, category is null ? null : code, receipt);

    private static HoldedLedgerLineInfo Line(int entry, int day, decimal debit, decimal credit, string? description) =>
        new(entry, 1, PleoBooking.PleoTreasury, Day(6, day), "entry", description, debit, credit);

    [HumansFact]
    public void Readiness_NeedsAmountReceiptAndAnOpenMappedAccount()
    {
        PleoBooking.Readiness(Entry("a", -10m), Open).Should().Be(PleoSpendState.Ready);
        PleoBooking.Readiness(Entry("a", null), Open).Should().Be(PleoSpendState.NoAmount);
        PleoBooking.Readiness(Entry("a", -10m, receipt: false), Open).Should().Be(PleoSpendState.NoReceipt);
        PleoBooking.Readiness(Entry("a", -10m, category: null), Open).Should().Be(PleoSpendState.NoCategory);
        PleoBooking.Readiness(Entry("a", -10m, receipt: false, category: null), Open)
            .Should().Be(PleoSpendState.NoReceiptNoCategory);
        PleoBooking.Readiness(Entry("a", -10m, code: null), Open).Should().Be(PleoSpendState.NotMapped);
        PleoBooking.Readiness(Entry("a", -10m, code: "62999999"), Open).Should().Be(PleoSpendState.NotMapped);
    }

    [HumansFact]
    public void Classify_LeavesOutLoadsOutOfPocketAndPending_AndMatchesTopUps()
    {
        var view = PleoBooking.Classify(
            [
                Entry("load", 500m, family: "WALLET"),
                Entry("oop", -56.98m, family: "OUT_OF_POCKET"),
                Entry("pend", -5m, status: "PENDING"),
                Entry("buy", -20m),
            ],
            [Line(1954, 11, 500m, 0m, "Transferencia")],
            new HashSet<int> { 1954 }, Open);

        view.Spend.Select(r => r.Entry.Id).Should().Equal("buy");
        view.Spend[0].State.Should().Be(PleoSpendState.Ready);
        view.TopUps.Should().ContainSingle().Which.HoldedEntry.Should().Be(1954);
    }

    [HumansFact]
    public void Classify_FindsBookedByPleoId_AndLegacyLinesByAmountAndDateOnce()
    {
        var view = PleoBooking.Classify(
            [
                Entry("byId", -30m, day: 1),
                Entry("amazon", -71.97m, day: 12),
                Entry("amazon2", -71.97m, day: 13),
                Entry("far", -15m, day: 20),
            ],
            [
                Line(4700, 1, 0m, 30m, "Pleo byId: MAKRO"),
                Line(2061, 12, 0m, 71.97m, "Amazon"),
                Line(2062, 1, 0m, 15m, "Something else"),
            ],
            new HashSet<int>(), Open);

        var byId = view.Spend.ToDictionary(r => r.Entry.Id, StringComparer.Ordinal);
        byId["byId"].Should().Match<PleoSpendRow>(r => r.State == PleoSpendState.Booked && r.HoldedEntry == 4700);
        byId["amazon"].Should().Match<PleoSpendRow>(r => r.State == PleoSpendState.Booked && r.HoldedEntry == 2061);
        byId["amazon2"].State.Should().Be(PleoSpendState.Ready, "one legacy line counts for one entry");
        byId["far"].State.Should().Be(PleoSpendState.Ready, "a legacy line too far in time is not a match");
    }

    [HumansFact]
    public void Classify_TopUpLineIsNeverTakenAsLegacySpend_AndRefundMatchesADebit()
    {
        var view = PleoBooking.Classify(
            [Entry("refund", 40m)],
            [Line(1955, 10, 40m, 0m, "Transferencia")],
            new HashSet<int> { 1955 }, Open);
        view.Spend[0].State.Should().Be(PleoSpendState.Ready);

        var legacy = PleoBooking.Classify(
            [Entry("refund", 40m)], [Line(3000, 10, 40m, 0m, "Abono")], new HashSet<int>(), Open);
        legacy.Spend[0].Should().Match<PleoSpendRow>(r => r.State == PleoSpendState.Booked && r.HoldedEntry == 3000);
    }

    [HumansFact]
    public void Classify_FlagsAPleoIdOnTwoEntriesAsADuplicate()
    {
        var view = PleoBooking.Classify(
            [Entry("a", -10m)],
            [Line(4700, 10, 0m, 10m, "Pleo a: MAKRO"), Line(4701, 10, 0m, 10m, "Pleo a: MAKRO")],
            new HashSet<int>(), Open);

        view.Duplicates.Should().ContainSingle().Which.HoldedEntries.Should().Equal(4700, 4701);
        view.Spend.Single().State.Should().Be(PleoSpendState.Booked);
    }

    [HumansFact]
    public async Task Book_PostsOldestFirst_SpendAndRefundOnOppositeSides_GrossWithPleoIdDescription()
    {
        GivenEntries(Entry("new", -10m, day: 20), Entry("old", -12.5m, day: 2), Entry("back", 7m, day: 5));

        var run = await Booking().BookAsync(1, null);
        run.Lines.Should().ContainSingle().Which.PleoId.Should().Be("old");
        await _write.Received(1).PostJournalEntryAsync(
            new LocalDate(2026, 6, 2), Food, PleoBooking.PleoTreasury, 12.5m, "Pleo old: MAKRO", CancellationToken.None);

        _ledger.Add(Line(5000, 2, 0m, 12.5m, "Pleo old: MAKRO"));
        await Booking().BookAsync(1, null);
        await _write.Received(1).PostJournalEntryAsync(
            new LocalDate(2026, 6, 5), PleoBooking.PleoTreasury, Food, 7m, "Pleo back: MAKRO", CancellationToken.None);
    }

    [HumansFact]
    public async Task Book_SkipsAnEntryAlreadyInTheLedger_ByPleoIdOrLegacyMatch()
    {
        GivenEntries(Entry("done", -30m, day: 1), Entry("amazon", -71.97m, day: 12), Entry("todo", -5m, day: 14));
        _ledger.Add(Line(4700, 1, 0m, 30m, "Pleo done: MAKRO"));
        _ledger.Add(Line(2061, 12, 0m, 71.97m, "Amazon"));

        var run = await Booking().BookAsync(null, null);

        run.Lines.Select(l => l.PleoId).Should().Equal("todo");
        await _write.ReceivedWithAnyArgs(1).PostJournalEntryAsync(default, default, default, default, default!, default);
        await _holded.Received().SyncAccountLedgerAsync(PleoBooking.PleoTreasury, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Book_SecondConcurrentRunIsRefused()
    {
        GivenEntries(Entry("a", -10m));
        using var release = new SemaphoreSlim(0);
        _write.PostJournalEntryAsync(default, default, default, default, default!, default)
            .ReturnsForAnyArgs(async _ => { await release.WaitAsync(); return new HoldedJournalEntryOutcome(true); });

        var first = Booking().BookAsync(1, null);
        var second = await Booking().BookAsync(1, null);
        release.Release();
        await first;

        second.Refused.Should().Contain("Another booking run");
        await _write.ReceivedWithAnyArgs(1).PostJournalEntryAsync(default, default, default, default, default!, default);
    }

    [HumansFact]
    public async Task Book_UnconfirmedPostIsReported_AndSkippedOnceTheLedgerShowsIt()
    {
        GivenEntries(Entry("a", -10m, day: 1), Entry("b", -11m, day: 2));
        _write.PostJournalEntryAsync(default, default, default, default, default!, default)
            .ReturnsForAnyArgs(new HoldedJournalEntryOutcome(false));

        var first = await Booking().BookAsync(1, null);
        first.Lines.Single().Should().Match<PleoBookingLine>(l =>
            l.PleoId == "a" && l.Outcome == PleoBookingOutcome.Unconfirmed && l.Error!.Contains("check in Holded"));

        _ledger.Add(Line(4800, 1, 0m, 10m, "Pleo a: MAKRO"));
        var second = await Booking().BookAsync(1, null);
        second.Lines.Single().PleoId.Should().Be("b");
    }

    [HumansFact]
    public async Task Book_PermanentFailureContinues_TransientFailureStops()
    {
        GivenEntries(Entry("a", -10m, day: 1), Entry("b", -11m, day: 2), Entry("c", -12m, day: 3));
        _write.PostJournalEntryAsync(default, default, default, default, default!, default)
            .ReturnsForAnyArgs(
                Task.FromException<HoldedJournalEntryOutcome>(new HoldedPermanentException("400")),
                Task.FromException<HoldedJournalEntryOutcome>(new HoldedTransientException("timeout")));

        var run = await Booking().BookAsync(null, null);

        run.Lines.Select(l => (l.PleoId, l.Outcome)).Should().Equal(
            ("a", PleoBookingOutcome.Failed), ("b", PleoBookingOutcome.Failed));
        run.Refused.Should().Contain("Stopped");
        await _write.ReceivedWithAnyArgs(2).PostJournalEntryAsync(default, default, default, default, default!, default);
    }
}
