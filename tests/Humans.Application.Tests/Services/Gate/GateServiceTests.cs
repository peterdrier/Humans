using AwesomeAssertions;
using Humans.Application;
using Humans.Application.Interfaces.EarlyEntry;
using Humans.Application.Interfaces.Gate;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Services.Gate;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Repositories.Gate;
using NodaTime;
using NSubstitute;

namespace Humans.Application.Tests.Services.Gate;

/// <summary>
/// Service-level coverage of <see cref="GateService"/> over a real
/// <see cref="GateRepository"/> (in-memory DB) with stubbed cross-section reads —
/// admit, dedupe, name-mismatch, child-with-adult, and the Early-Entry cutoff
/// branches, including that a client "ID confirmed" never overrides a STOP.
/// </summary>
public class GateServiceTests : ServiceTestHarness
{
    private const string Barcode = "TT-ABC-123";
    private static readonly Guid GuestId = Guid.NewGuid();
    private static readonly Guid AgentId = Guid.NewGuid();

    private readonly ITicketServiceRead _tickets = Substitute.For<ITicketServiceRead>();
    private readonly IEarlyEntryService _earlyEntry = Substitute.For<IEarlyEntryService>();
    private readonly IBurnSettingsService _burn = Substitute.For<IBurnSettingsService>();
    private readonly GateService _svc;

    public GateServiceTests()
    {
        _burn.GetActiveAsync(Arg.Any<CancellationToken>()).Returns((BurnSettingsInfo?)null);
        _earlyEntry.GetForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((UserEarlyEntry?)null);
        _svc = new GateService(new GateRepository(DbFactory), _tickets, _earlyEntry, _burn, Clock);

        // Baseline: an admin has set the cutoff in the past, so general entry is open.
        // Seeded directly (sync) since the ctor can't await; Db shares the in-memory
        // store with the repository's factory. Before-cutoff tests override this, and
        // the unconfigured-cutoff case is tested explicitly. Without a configured
        // cutoff every scan fails safe to AMBER.
        Db.Set<GateSettings>().Add(new GateSettings
        {
            Id = 1,
            GeneralEntryOpensAt = Clock.GetCurrentInstant().Minus(Duration.FromHours(1)),
            MinorAgeThresholdYears = 16,
        });
        Db.SaveChanges();
    }

    private void StubTicket(
        TicketAttendeeStatus status = TicketAttendeeStatus.Valid,
        Guid? matchedUserId = null,
        string barcode = Barcode)
    {
        var attendee = new TicketAttendeeInfo(
            Id: Guid.NewGuid(),
            VendorTicketId: "v1",
            AttendeeName: "Jane Donovan",
            AttendeeEmail: "jane@example.com",
            TicketTypeName: "GA",
            Price: 100m,
            Status: status,
            MatchedUserId: matchedUserId,
            Barcode: barcode);

        var order = new TicketOrderInfo(
            Id: Guid.NewGuid(),
            VendorOrderId: "o1",
            BuyerName: "Jane Donovan",
            BuyerEmail: "jane@example.com",
            TotalAmount: 100m,
            Currency: "EUR",
            DiscountCode: null,
            PaymentStatus: TicketPaymentStatus.Paid,
            VendorEventId: "evt",
            PurchasedAt: Clock.GetCurrentInstant(),
            MatchedUserId: matchedUserId,
            IsCurrentEvent: true,
            Attendees: new[] { attendee });

        _tickets.GetTicketOrdersAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TicketOrderInfo> { order });
    }

    private Task<GateDecisionResult> Record(bool idConfirmed, bool child = false) =>
        _svc.RecordDecisionAsync(
            new GateDecisionInput(Barcode, idConfirmed, child, LaneId: "L1", ClientScanAt: null, Note: null),
            AgentId);

    [HumansFact]
    public async Task Evaluate_UnknownBarcode_IsInvalid()
    {
        _tickets.GetTicketOrdersAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TicketOrderInfo>());

        (await _svc.EvaluateAsync("nope")).Outcome.Should().Be(GatePreCheckOutcome.Invalid);
    }

    [HumansFact]
    public async Task Evaluate_ValidAfterCutoff_NeedsIdCheck_WithName()
    {
        StubTicket();

        var r = await _svc.EvaluateAsync(Barcode);

        r.Outcome.Should().Be(GatePreCheckOutcome.NeedsIdCheck);
        r.GuestName.Should().Be("Jane Donovan");
        r.TicketTypeName.Should().Be("GA");
    }

    [HumansFact]
    public async Task Evaluate_MatchesBarcodeCaseInsensitivelyAfterTrim()
    {
        StubTicket(barcode: Barcode);

        (await _svc.EvaluateAsync($"  {Barcode.ToLowerInvariant()}  "))
            .Outcome.Should().Be(GatePreCheckOutcome.NeedsIdCheck);
    }

    [HumansFact]
    public async Task RecordDecision_IdConfirmed_Admits_ThenDuplicateOnRescan()
    {
        StubTicket();

        var first = await Record(idConfirmed: true);
        first.Verdict.Should().Be(GateVerdict.Admitted);

        var second = await Record(idConfirmed: true);
        second.Verdict.Should().Be(GateVerdict.RejectedDuplicate);
        second.PreviousAdmitByUserId.Should().Be(AgentId);
    }

    [HumansFact]
    public async Task RecordDecision_IdRejected_IsNameMismatch_AndDoesNotBurnTicket()
    {
        StubTicket();

        var r = await Record(idConfirmed: false);
        r.Verdict.Should().Be(GateVerdict.RejectedNameMismatch);

        // Ticket not burned: a corrected re-scan can still be admitted.
        (await _svc.EvaluateAsync(Barcode)).Outcome.Should().Be(GatePreCheckOutcome.NeedsIdCheck);
    }

    [HumansFact]
    public async Task RecordDecision_ChildWithAdult_Admits()
    {
        StubTicket();

        (await Record(idConfirmed: false, child: true))
            .Verdict.Should().Be(GateVerdict.AdmittedChildWithAdult);
    }

    [HumansFact]
    public async Task CutoffNotConfigured_IsAmberUnresolved_NeverSilentlyAdmits()
    {
        StubTicket();
        // Reset to the unconfigured sentinel (the ctor seeded an open cutoff).
        await _svc.SaveSettingsAsync(new GateSettingsDto(Instant.MinValue, 16));

        (await _svc.EvaluateAsync(Barcode)).Outcome.Should().Be(GatePreCheckOutcome.CutoffNotConfigured);

        // Even with the agent confirming the ID, an unset cutoff escalates — no admit.
        (await Record(idConfirmed: true)).Verdict.Should().Be(GateVerdict.Unresolved);
    }

    [HumansFact]
    public async Task GetSettings_ReportsCutoffConfigured()
    {
        (await _svc.GetSettingsAsync()).CutoffConfigured.Should().BeTrue();   // ctor seeded a real cutoff

        await _svc.SaveSettingsAsync(new GateSettingsDto(Instant.MinValue, 16));
        (await _svc.GetSettingsAsync()).CutoffConfigured.Should().BeFalse();
    }

    [HumansFact]
    public async Task BeforeCutoff_NoEarlyEntry_RejectsTooEarly_EvenIfIdConfirmed()
    {
        StubTicket(matchedUserId: GuestId);
        await _svc.SaveSettingsAsync(new GateSettingsDto(
            Clock.GetCurrentInstant().Plus(Duration.FromHours(6)), 16));

        (await _svc.EvaluateAsync(Barcode)).Outcome.Should().Be(GatePreCheckOutcome.TooEarly);

        (await Record(idConfirmed: true))
            .Verdict.Should().Be(GateVerdict.RejectedTooEarly);
    }

    [HumansFact]
    public async Task BeforeCutoff_Unmatched_IsAmberUnresolved()
    {
        StubTicket(matchedUserId: null);
        await _svc.SaveSettingsAsync(new GateSettingsDto(
            Clock.GetCurrentInstant().Plus(Duration.FromHours(6)), 16));

        (await _svc.EvaluateAsync(Barcode)).Outcome.Should().Be(GatePreCheckOutcome.EarlyEntryUnknown);
    }

    [HumansFact]
    public async Task BeforeCutoff_EarlyEntryToday_AdmitsEarly()
    {
        StubTicket(matchedUserId: GuestId);
        await _svc.SaveSettingsAsync(new GateSettingsDto(
            Clock.GetCurrentInstant().Plus(Duration.FromHours(6)), 16));
        _earlyEntry.GetForUserAsync(GuestId, Arg.Any<CancellationToken>())
            .Returns(new UserEarlyEntry(Clock.GetCurrentInstant().InUtc().Date, new[] { "Build crew" }));

        (await Record(idConfirmed: true))
            .Verdict.Should().Be(GateVerdict.AdmittedEarly);
    }

    [HumansFact]
    public async Task Leaderboard_TalliesAdmitsAndRejects()
    {
        StubTicket();
        await Record(idConfirmed: true);   // admit
        await Record(idConfirmed: true);   // duplicate reject

        var board = await _svc.GetLeaderboardAsync(Instant.MinValue);

        board.TotalAdmitted.Should().Be(1);
        board.TotalScanned.Should().Be(2);
        board.Rows.Should().ContainSingle(r => r.ScannedByUserId == AgentId && r.Admitted == 1 && r.Rejected == 1);
    }

    private static int SliceCount(IReadOnlyList<UserDataSlice> slices) =>
        ((System.Collections.ICollection)slices.Single().Data!).Count;

    [HumansFact]
    public async Task Gdpr_ContributesScansForGuestAndScanner()
    {
        StubTicket(matchedUserId: GuestId);
        await Record(idConfirmed: true);   // GuestUserId = GuestId, ScannedByUserId = AgentId

        var guestExport = await _svc.ContributeForUserAsync(GuestId, default);
        var scannerExport = await _svc.ContributeForUserAsync(AgentId, default);

        guestExport.Single().SectionName.Should().Be(GdprExportSections.GateScans);
        SliceCount(guestExport).Should().Be(1);
        SliceCount(scannerExport).Should().Be(1);
        SliceCount(await _svc.ContributeForUserAsync(Guid.NewGuid(), default)).Should().Be(0);
    }

    [HumansFact]
    public async Task Merge_ReassignsScansToSurvivor()
    {
        StubTicket(matchedUserId: GuestId);
        await Record(idConfirmed: true);
        var survivor = Guid.NewGuid();

        await _svc.ReassignAsync(GuestId, survivor, actorUserId: Guid.NewGuid(), Clock.GetCurrentInstant(), default);

        SliceCount(await _svc.ContributeForUserAsync(GuestId, default)).Should().Be(0);
        SliceCount(await _svc.ContributeForUserAsync(survivor, default)).Should().Be(1);
    }

    [HumansFact]
    public async Task Retention_PurgesScansBeforeCutoff()
    {
        StubTicket(matchedUserId: GuestId);
        await Record(idConfirmed: true);

        var removed = await _svc.PurgeScansBeforeAsync(Clock.GetCurrentInstant().Plus(Duration.FromMinutes(1)));

        removed.Should().Be(1);
        SliceCount(await _svc.ContributeForUserAsync(GuestId, default)).Should().Be(0);
    }
}
