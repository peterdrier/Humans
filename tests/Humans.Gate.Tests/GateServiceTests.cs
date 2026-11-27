using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.EarlyEntry.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Settings.Contracts;
using Humans.Tickets.Contracts;
using Humans.Users.Contracts;
using Humans.Base.Constants;
using Humans.Gate.Data;
using Humans.Gate.Domain;
using Humans.Gate.Models;
using Humans.Gate.Services;
using Humans.Gate.Services.Stores;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NodaTime.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Humans.Gate.Tests;

/// <summary>
/// Runs <see cref="GateService"/> over a real <see cref="GateRepository"/> on an in-memory DB with stubbed cross-section reads.
/// </summary>
public class GateServiceTests
{
    [HumansTheory]
    [Xunit.InlineData("23505", "ix_gate_scan_events_admit_dedupe_key", true)]
    [Xunit.InlineData("23505", "PK_gate_scan_events", false)]
    [Xunit.InlineData("23503", "ix_gate_scan_events_admit_dedupe_key", false)]
    public async Task Insert_OnlyTreatsExpectedConstraintAsDuplicate(string sqlState, string constraint, bool duplicate)
    {
        var failure = new DbUpdateException("Save failed", new Npgsql.PostgresException(
            "Constraint violation", "ERROR", "ERROR", sqlState, constraintName: constraint));
        var options = new DbContextOptionsBuilder<GateDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .AddInterceptors(new FailedInsert(failure)).Options;
        var repo = new GateRepository(new TestDbContextFactory<GateDbContext>(options));
        var row = new GateScanEvent { Id = Guid.NewGuid(), Barcode = "barcode", AdmitDedupeKey = "barcode" };
        var insert = () => repo.RecordScanAsync(row, Xunit.TestContext.Current.CancellationToken);

        if (duplicate)
            (await insert()).Should().Be(GateRecordOutcome.DuplicateAdmitRejected);
        else
            (await insert.Should().ThrowAsync<DbUpdateException>()).Which.Should().BeSameAs(failure);
    }

    private sealed class FailedInsert(DbUpdateException failure) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default) => throw failure;
    }

    // Owned rather than the shared UsersDbContext-based harness, which would grant this project
    // InternalsVisibleTo on it. Field initialisers run in declaration order; each test gets its own store.
    private readonly DbContextOptions<GateDbContext> _dbOptions =
        new DbContextOptionsBuilder<GateDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 3, 1, 12, 0));
    private readonly GateDbContext _gateDb;
    private readonly TestDbContextFactory<GateDbContext> _gateDbFactory;

    private const string Barcode = "TT-ABC-123";
    private static readonly Guid GuestId = Guid.NewGuid();
    private static readonly Guid AgentId = Guid.NewGuid();

    private readonly ITicketService _tickets = Substitute.For<ITicketService>();
    private readonly ITicketGateLookup _issued = Substitute.For<ITicketGateLookup>();
    private readonly IEarlyEntryService _earlyEntry = Substitute.For<IEarlyEntryService>();
    private readonly ISettingsService _burn = Substitute.For<ISettingsService>();
    private readonly IAuditLogService _auditLog = Substitute.For<IAuditLogService>();
    private readonly ITicketWrite _users = Substitute.For<ITicketWrite>();
    private readonly IUserService _people = Substitute.For<IUserService>();
    private readonly IConfiguration _config = new ConfigurationBuilder().AddInMemoryCollection().Build();
    private const string SupervisorPin = "2468";
    private readonly GateService _svc;

    public GateServiceTests()
    {
        _config["Gate:SupervisorPin"] = SupervisorPin;
        _gateDb = new GateDbContext(_dbOptions);
        _gateDbFactory = new TestDbContextFactory<GateDbContext>(_dbOptions);

        _burn.GetActiveEventSettings().Returns((EventSettingsInfo?)null);
        _earlyEntry.GetForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((EarlyEntryRosterRow?)null);
        _svc = new GateService(new GateRepository(_gateDbFactory), _tickets, _earlyEntry, _burn, _users,
            _issued, _auditLog, new GatePinThrottle(new MemoryCache(new MemoryCacheOptions()), _clock), _config, NullLogger<GateService>.Instance, _clock, _people);

        // Baseline: a past cutoff, so general entry is open; without one every scan fails safe to
        // AMBER. Seeded sync (the ctor can't await) into the store the repository's factory shares.
        _gateDb.GateSettings.Add(new GateSettings
        {
            Id = 1,
            GeneralEntryOpensAt = _clock.GetCurrentInstant().Minus(Duration.FromHours(1)),
        });
        _gateDb.SaveChanges();
    }

    private TicketOrderInfo StubTicket(
        TicketAttendeeStatus status = TicketAttendeeStatus.Valid,
        Guid? matchedUserId = null,
        string barcode = Barcode,
        Guid? attendeeId = null,
        Instant? checkedInAt = null,
        string vendorTicketId = "v1")
    {
        var attendee = new TicketAttendeeInfo(
            Id: attendeeId ?? Guid.NewGuid(),
            VendorTicketId: vendorTicketId,
            AttendeeName: "Jane Donovan",
            AttendeeEmail: "jane@example.com",
            TicketTypeName: "GA",
            Status: status,
            MatchedUserId: matchedUserId,
            Barcode: barcode,
            CheckedInAt: checkedInAt);

        var order = new TicketOrderInfo(
            Id: Guid.NewGuid(),
            BuyerEmail: "jane@example.com",
            TotalAmount: 100m,
            PaymentStatus: TicketPaymentStatus.Paid,
            VendorEventId: "evt",
            PurchasedAt: _clock.GetCurrentInstant(),
            MatchedUserId: matchedUserId,
            IsCurrentEvent: true,
            Attendees: new[] { attendee });

        _tickets.GetTicketOrdersAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TicketOrderInfo> { order });
        return order;
    }

    private Task<GateDecisionResult> RecordAsync(bool idConfirmed, bool child = false, bool overrideEarly = false, string? pin = null) =>
        _svc.RecordDecisionAsync(
            new GateDecisionInput(Barcode, idConfirmed, child, LaneId: "L1", ClientScanAt: null, Note: null,
                OverrideEarly: overrideEarly, SupervisorPin: pin),
            AgentId);

    [HumansTheory]
    [Xunit.InlineData(null, "2468", (int)GateOverrideFailure.NotConfigured)]
    [Xunit.InlineData("", "2468", (int)GateOverrideFailure.NotConfigured)]
    [Xunit.InlineData("2468", null, (int)GateOverrideFailure.InvalidPin)]
    [Xunit.InlineData("2468", "", (int)GateOverrideFailure.InvalidPin)]
    [Xunit.InlineData("2468", "0000", (int)GateOverrideFailure.InvalidPin)]
    public async Task Override_InvalidOrUnconfiguredPin_RefusesBeforeScan(
        string? configured, string? supplied, int failure)
    {
        _config["Gate:SupervisorPin"] = configured;
        StubTicket(matchedUserId: GuestId);

        var result = await RecordAsync(idConfirmed: true, overrideEarly: true, pin: supplied);

        result.OverrideFailure.Should().Be((GateOverrideFailure)failure);
        result.Verdict.Should().Be(GateVerdict.Unresolved);
        _gateDb.GateScanEvents.AsNoTracking().Should().BeEmpty();
        await _tickets.DidNotReceive().GetTicketOrdersAsync(Arg.Any<CancellationToken>());
        await _users.DidNotReceive().RecordParticipationAsync(
            Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<ParticipationStatus>(), Arg.Any<Instant?>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Override_ThrottleLocksOnlyOverrides_ForTheFullFifteenMinutes()
    {
        StubTicket();
        for (var i = 0; i < 5; i++)
            (await RecordAsync(false, overrideEarly: true, pin: "0000"))
                .OverrideFailure.Should().Be(GateOverrideFailure.InvalidPin);

        var locked = await RecordAsync(false, overrideEarly: true, pin: SupervisorPin);
        locked.OverrideFailure.Should().Be(GateOverrideFailure.LockedOut);
        locked.RetryAfterSeconds.Should().Be(900);
        _gateDb.GateScanEvents.AsNoTracking().Should().BeEmpty();

        _clock.Advance(Duration.FromMinutes(14));
        var stillLocked = await RecordAsync(false, child: true, pin: SupervisorPin);
        stillLocked.OverrideFailure.Should().Be(GateOverrideFailure.LockedOut);
        stillLocked.RetryAfterSeconds.Should().Be(60);

        (await RecordAsync(false)).Verdict.Should().Be(GateVerdict.RejectedNameMismatch);
        _clock.Advance(Duration.FromMinutes(1) + Duration.FromSeconds(1));
        (await RecordAsync(false, child: true, pin: SupervisorPin))
            .Verdict.Should().Be(GateVerdict.AdmittedChildWithAdult);
        var admit = await _gateDb.GateScanEvents.AsNoTracking().SingleAsync(s => s.AdmitDedupeKey != null,
            Xunit.TestContext.Current.CancellationToken);
        admit.OverrideByUserId.Should().Be(AgentId);
    }

    [HumansFact]
    public async Task Override_CorrectPin_ClearsPreviousFailures()
    {
        StubTicket(status: TicketAttendeeStatus.Void);
        for (var i = 0; i < 4; i++)
            await RecordAsync(false, overrideEarly: true, pin: "0000");

        var validPin = await RecordAsync(true, overrideEarly: true, pin: SupervisorPin);
        validPin.OverrideFailure.Should().BeNull();
        validPin.Verdict.Should().Be(GateVerdict.RejectedInvalid);
        for (var i = 0; i < 4; i++)
            (await RecordAsync(false, overrideEarly: true, pin: "0000"))
                .OverrideFailure.Should().Be(GateOverrideFailure.InvalidPin);
        (await RecordAsync(true, overrideEarly: true, pin: SupervisorPin))
            .OverrideFailure.Should().BeNull();
        _gateDb.GateScanEvents.AsNoTracking().Should().OnlyContain(s => s.OverrideByUserId == null);
    }

    [HumansFact]
    public async Task Evaluate_UnknownBarcode_IsInvalid()
    {
        _tickets.GetTicketOrdersAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TicketOrderInfo>());

        (await _svc.EvaluateAsync("nope")).Outcome.Should().Be(GatePreCheckOutcome.Invalid);
    }

    [HumansFact]
    public async Task Evaluate_ValidAfterCutoff_NeedsIdCheck()
    {
        StubTicket();

        var r = await _svc.EvaluateAsync(Barcode);

        r.Outcome.Should().Be(GatePreCheckOutcome.NeedsIdCheck);
        r.GuestName.Should().Be("Jane Donovan");
        r.TicketTypeName.Should().Be("GA");
    }

    [HumansFact]
    public async Task Evaluate_VendorCheckedInTimestamp_IsDuplicate()
    {
        StubTicket(checkedInAt: _clock.GetCurrentInstant());

        (await _svc.EvaluateAsync(Barcode)).Outcome.Should().Be(GatePreCheckOutcome.Duplicate);
    }

    [HumansFact]
    public async Task Evaluate_MatchesBarcodeCaseInsensitivelyAfterTrim()
    {
        StubTicket(barcode: Barcode);

        (await _svc.EvaluateAsync($"  {Barcode.ToLowerInvariant()}  "))
            .Outcome.Should().Be(GatePreCheckOutcome.NeedsIdCheck);
    }

    [HumansTheory]
    [Xunit.InlineData("barcode", 128, false)]
    [Xunit.InlineData("lane", 64, false)]
    [Xunit.InlineData("note", 1000, false)]
    [Xunit.InlineData("barcode", 128, true)]
    [Xunit.InlineData("lane", 64, true)]
    [Xunit.InlineData("note", 1000, true)]
    public async Task RecordDecision_OverlongInput_RefusesBeforeWriting(string field, int limit, bool supplementary)
    {
        var text = string.Concat(Enumerable.Repeat(supplementary ? "😀" : "X", limit + 1));
        var input = new GateDecisionInput(field is "barcode" ? text : Barcode, true, false,
            field is "lane" ? text : "L1", null, field is "note" ? text : null);
        StubTicket(barcode: input.Barcode);

        var result = await _svc.RecordDecisionAsync(input, AgentId, Xunit.TestContext.Current.CancellationToken);

        result.InputTooLong.Should().BeTrue();
        result.Verdict.Should().Be(GateVerdict.Unresolved);
        (await _gateDb.GateScanEvents.CountAsync(Xunit.TestContext.Current.CancellationToken)).Should().Be(0);
        _users.ReceivedCalls().Should().BeEmpty();
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task RecordDecision_MaxLengthValues_SavedWhole(bool supplementary)
    {
        var unit = supplementary ? "😀" : "X";
        var barcode = string.Concat(Enumerable.Repeat(unit, 128));
        var lane = string.Concat(Enumerable.Repeat(unit, 64));
        var note = string.Concat(Enumerable.Repeat(unit, 1000));
        StubTicket(barcode: barcode);

        var result = await _svc.RecordDecisionAsync(new GateDecisionInput("  " + barcode + "  ", true, false,
            lane, null, note), AgentId, Xunit.TestContext.Current.CancellationToken);

        result.Verdict.Should().Be(GateVerdict.Admitted);
        var saved = await _gateDb.GateScanEvents.SingleAsync(Xunit.TestContext.Current.CancellationToken);
        saved.Barcode.Should().Be(barcode);
        saved.LaneId.Should().Be(lane);
        saved.Note.Should().Be(note);
    }

    [HumansFact]
    public async Task RecordDecision_Rescan_IsDuplicate()
    {
        StubTicket();

        var first = await RecordAsync(idConfirmed: true);
        first.Verdict.Should().Be(GateVerdict.Admitted);

        var second = await RecordAsync(idConfirmed: true, overrideEarly: true, pin: SupervisorPin);
        second.Verdict.Should().Be(GateVerdict.RejectedDuplicate);
        second.PreviousAdmitByUserId.Should().Be(AgentId);
    }

    [HumansFact]
    public async Task RecordDecision_IdRejected_IsNameMismatch()
    {
        StubTicket();

        var r = await RecordAsync(idConfirmed: false);
        r.Verdict.Should().Be(GateVerdict.RejectedNameMismatch);

        // Ticket not burned: a corrected re-scan can still be admitted.
        (await _svc.EvaluateAsync(Barcode)).Outcome.Should().Be(GatePreCheckOutcome.NeedsIdCheck);
    }

    [HumansFact]
    public async Task RecordDecision_ChildWithSupervisor_Admits()
    {
        StubTicket();

        // Child waivers verify the PIN even without an early-override flag.
        (await RecordAsync(idConfirmed: false, child: true, pin: SupervisorPin))
            .Verdict.Should().Be(GateVerdict.AdmittedChildWithAdult);
    }

    [HumansFact]
    public async Task RecordDecision_ChildWithoutSupervisor_DoesNotAdmit()
    {
        StubTicket();

        var result = await RecordAsync(idConfirmed: false, child: true);
        result.OverrideFailure.Should().Be(GateOverrideFailure.InvalidPin);
        _gateDb.GateScanEvents.AsNoTracking().Should().BeEmpty();
    }

    private void StubActiveEvent(int year = 2026) =>
        _burn.GetActiveEventSettings().Returns(BurnFixtures.Burn(
            eventName: "Test",
            year: year,
            gateOpeningDate: new LocalDate(year, 3, 1)));

    [HumansFact]
    public async Task RecordDecision_Admit_MarksParticipationAttended()
    {
        StubTicket(matchedUserId: GuestId);
        StubActiveEvent(year: 2026);

        var r = await RecordAsync(idConfirmed: true);

        r.Verdict.Should().Be(GateVerdict.Admitted);
        // CancellationToken.None pinned: the admit is durable, so a kiosk disconnect
        // (aborted request token) must not skip the projection.
        await _users.Received(1).RecordParticipationAsync(
            GuestId, 2026, ParticipationStatus.Attended,
            _clock.GetCurrentInstant(), CancellationToken.None);
    }

    [HumansFact]
    public async Task RecordDecision_ParticipationWriteThrows_StillAdmits()
    {
        // The projection races the ticket sync on the same (UserId, Year) row; its
        // failure must never turn an already-recorded admit into an error response.
        StubTicket(matchedUserId: GuestId);
        StubActiveEvent();
        _users.RecordParticipationAsync(
                default, default, default, default)
            .ThrowsAsyncForAnyArgs(new InvalidOperationException("unique index collision"));

        (await RecordAsync(idConfirmed: true)).Verdict.Should().Be(GateVerdict.Admitted);
    }

    [HumansFact]
    public async Task RecordDecision_Reject_DoesNotTouchParticipation()
    {
        StubTicket(matchedUserId: GuestId);
        StubActiveEvent();

        (await RecordAsync(idConfirmed: false)).Verdict.Should().Be(GateVerdict.RejectedNameMismatch);

        await _users.DidNotReceiveWithAnyArgs()
            .RecordParticipationAsync(default, default, default, default);
    }

    [HumansFact]
    public async Task RecordDecision_UnmatchedGuest_SkipsParticipation()
    {
        // Barcode not matched to a Humans account: there is no user to mark Attended.
        StubTicket(matchedUserId: null);
        StubActiveEvent();

        (await RecordAsync(idConfirmed: true)).Verdict.Should().Be(GateVerdict.Admitted);

        await _users.DidNotReceiveWithAnyArgs()
            .RecordParticipationAsync(default, default, default, default);
    }

    [HumansFact]
    public async Task RecordDecision_NoActiveEvent_StillAdmits()
    {
        // No active event → no year to record against; the admit itself must not fail.
        StubTicket(matchedUserId: GuestId);

        (await RecordAsync(idConfirmed: true)).Verdict.Should().Be(GateVerdict.Admitted);

        await _users.DidNotReceiveWithAnyArgs()
            .RecordParticipationAsync(default, default, default, default);
    }

    [HumansFact]
    public async Task CutoffNotConfigured_IsAmberUnresolved_NeverSilentlyAdmits()
    {
        StubTicket();
        // Reset to the unconfigured sentinel (the ctor seeded an open cutoff).
        await _svc.SaveSettingsAsync(new GateSettingsDto(Instant.MinValue));

        (await _svc.EvaluateAsync(Barcode)).Outcome.Should().Be(GatePreCheckOutcome.CutoffNotConfigured);

        // Even a verified override cannot admit while the cutoff is unset.
        (await RecordAsync(idConfirmed: true)).Verdict.Should().Be(GateVerdict.Unresolved);
        (await RecordAsync(idConfirmed: true, overrideEarly: true, pin: SupervisorPin))
            .Verdict.Should().Be(GateVerdict.Unresolved);
    }

    [HumansFact]
    public async Task GetSettings_ReportsCutoffConfigured()
    {
        (await _svc.GetSettingsAsync()).CutoffConfigured.Should().BeTrue();   // ctor seeded a real cutoff

        await _svc.SaveSettingsAsync(new GateSettingsDto(Instant.MinValue));
        (await _svc.GetSettingsAsync()).CutoffConfigured.Should().BeFalse();
    }

    [HumansFact]
    public async Task BeforeCutoff_NoEarlyEntryIdConfirmed_RejectsTooEarly()
    {
        StubTicket(matchedUserId: GuestId);
        await _svc.SaveSettingsAsync(new GateSettingsDto(
            _clock.GetCurrentInstant().Plus(Duration.FromHours(6))));

        (await _svc.EvaluateAsync(Barcode)).Outcome.Should().Be(GatePreCheckOutcome.TooEarly);

        (await RecordAsync(idConfirmed: true))
            .Verdict.Should().Be(GateVerdict.RejectedTooEarly);
    }

    [HumansFact]
    public async Task BeforeCutoff_TooEarlyWithOverride_AdmitsEarlyOverride()
    {
        StubTicket(matchedUserId: GuestId);
        await _svc.SaveSettingsAsync(new GateSettingsDto(
            _clock.GetCurrentInstant().Plus(Duration.FromHours(6))));

        (await _svc.EvaluateAsync(Barcode)).Outcome.Should().Be(GatePreCheckOutcome.TooEarly);

        // A recorded supervisor override turns the too-early STOP into an attributable early admit.
        (await RecordAsync(idConfirmed: false, overrideEarly: true, pin: SupervisorPin))
            .Verdict.Should().Be(GateVerdict.AdmittedEarlyOverride);
    }

    [HumansFact]
    public async Task Evaluate_TooEarly_CarriesBothEntryDates()
    {
        StubTicket(matchedUserId: GuestId);
        var cutoff = _clock.GetCurrentInstant().Plus(Duration.FromHours(6));
        await _svc.SaveSettingsAsync(new GateSettingsDto(cutoff));
        // Holds a *later-day* Early Entry grant (tomorrow) — the distinguishing too-early sub-case.
        var tomorrow = _clock.GetCurrentInstant().InUtc().Date.PlusDays(1);
        _earlyEntry.GetForUserAsync(GuestId, Arg.Any<CancellationToken>())
            .Returns(new EarlyEntryRosterRow(GuestId, tomorrow, new[] { "Crew" }, false));

        var r = await _svc.EvaluateAsync(Barcode);

        r.Outcome.Should().Be(GatePreCheckOutcome.TooEarly);
        r.EarliestEntryDate.Should().Be(tomorrow);
        r.Today.Should().Be(_clock.GetCurrentInstant().InUtc().Date);
        r.GeneralEntryDate.Should().Be(cutoff.InUtc().Date);
    }

    [HumansFact]
    public async Task BeforeCutoff_Unmatched_IsAmberUnresolved()
    {
        StubTicket(matchedUserId: null);
        await _svc.SaveSettingsAsync(new GateSettingsDto(
            _clock.GetCurrentInstant().Plus(Duration.FromHours(6))));

        (await _svc.EvaluateAsync(Barcode)).Outcome.Should().Be(GatePreCheckOutcome.EarlyEntryUnknown);
    }

    [HumansFact]
    public async Task BeforeCutoff_UnmatchedWithoutOverride_StaysUnresolved()
    {
        StubTicket(matchedUserId: null);
        await _svc.SaveSettingsAsync(new GateSettingsDto(
            _clock.GetCurrentInstant().Plus(Duration.FromHours(6))));

        // Even a client-asserted "ID confirmed" never turns the unconfirmed-EE amber into an admit.
        (await RecordAsync(idConfirmed: true))
            .Verdict.Should().Be(GateVerdict.Unresolved);
    }

    [HumansFact]
    public async Task BeforeCutoff_UnmatchedWithOverride_AdmitsEarlyOverride()
    {
        StubTicket(matchedUserId: null);
        await _svc.SaveSettingsAsync(new GateSettingsDto(
            _clock.GetCurrentInstant().Plus(Duration.FromHours(6))));

        // A recorded supervisor override admits an unconfirmed-EE scan, mirroring the too-early case.
        (await RecordAsync(idConfirmed: false, overrideEarly: true, pin: SupervisorPin))
            .Verdict.Should().Be(GateVerdict.AdmittedEarlyOverride);
    }

    [HumansFact]
    public async Task BeforeCutoff_EarlyEntryToday_AdmitsEarly()
    {
        StubTicket(matchedUserId: GuestId);
        await _svc.SaveSettingsAsync(new GateSettingsDto(
            _clock.GetCurrentInstant().Plus(Duration.FromHours(6))));
        _earlyEntry.GetForUserAsync(GuestId, Arg.Any<CancellationToken>())
            .Returns(new EarlyEntryRosterRow(GuestId, _clock.GetCurrentInstant().InUtc().Date, new[] { "Build crew" }, false));

        (await RecordAsync(idConfirmed: true))
            .Verdict.Should().Be(GateVerdict.AdmittedEarly);
    }

    private static int SliceCount(IReadOnlyList<UserDataSlice> slices) =>
        ((System.Collections.ICollection)slices.Single().Data!).Count;

    [HumansFact]
    public async Task Gdpr_GuestOrScanner_ContributesScans()
    {
        StubTicket(matchedUserId: GuestId);
        await RecordAsync(idConfirmed: true);

        var guestExport = await _svc.ContributeForUserAsync(GuestId, default);
        var scannerExport = await _svc.ContributeForUserAsync(AgentId, default);

        guestExport.Single().SectionName.Should().Be(GateService.GateScans);
        SliceCount(guestExport).Should().Be(1);
        SliceCount(scannerExport).Should().Be(1);
        SliceCount(await _svc.ContributeForUserAsync(Guid.NewGuid(), default)).Should().Be(0);
    }

    [HumansFact]
    public async Task Gdpr_Export_OmitsBarcode()
    {
        var supervisor = AgentId;
        StubTicket(matchedUserId: GuestId);
        await RecordAsync(idConfirmed: true, overrideEarly: true, pin: SupervisorPin);

        var json = System.Text.Json.JsonSerializer.Serialize(
            (await _svc.ContributeForUserAsync(GuestId, default)).Single().Data);

        json.Should().NotContain(Barcode)
            .And.NotContain(AgentId.ToString())
            .And.NotContain(supervisor.ToString());
    }

    [HumansFact]
    public async Task RecordDecision_ClientScanAtNeverDecidesTheVerdict()
    {
        StubTicket(matchedUserId: GuestId);
        await _svc.SaveSettingsAsync(new GateSettingsDto(
            _clock.GetCurrentInstant().Plus(Duration.FromHours(6))));

        // A device clock claiming a post-cutoff scan cannot admit early: server time decides.
        var r = await _svc.RecordDecisionAsync(
            new GateDecisionInput(Barcode, IdConfirmed: true, ChildWithAdult: false, LaneId: "L1",
                ClientScanAt: _clock.GetCurrentInstant().Plus(Duration.FromHours(12)), Note: null),
            AgentId);

        r.Verdict.Should().Be(GateVerdict.RejectedTooEarly);
    }

    [HumansFact]
    public async Task Merge_ReassignsScansToSurvivor()
    {
        StubTicket(matchedUserId: GuestId);
        await RecordAsync(idConfirmed: true);
        var survivor = Guid.NewGuid();

        await _svc.ReassignAsync(GuestId, survivor, actorUserId: Guid.NewGuid(), _clock.GetCurrentInstant(), default);

        SliceCount(await _svc.ContributeForUserAsync(GuestId, default)).Should().Be(0);
        SliceCount(await _svc.ContributeForUserAsync(survivor, default)).Should().Be(1);
    }

    [HumansFact]
    public async Task Erase_ScannerAttribution_IsKept()
    {
        var supervisor = AgentId;
        StubTicket(matchedUserId: GuestId);
        await RecordAsync(idConfirmed: true, overrideEarly: true, pin: SupervisorPin);

        // Erase the guest: their admission link goes.
        await _svc.EraseForUserAsync(GuestId, default);
        var row = await _gateDb.GateScanEvents.AsNoTracking().SingleAsync(Xunit.TestContext.Current.CancellationToken);
        row.GuestUserId.Should().BeNull();

        // Erase the override authorizer: that link goes too.
        await _svc.EraseForUserAsync(supervisor, default);
        (await _gateDb.GateScanEvents.AsNoTracking().SingleAsync(Xunit.TestContext.Current.CancellationToken)).OverrideByUserId.Should().BeNull();

        // Erase the scanner: ScannedByUserId stays — the Art. 17(3) operational record.
        await _svc.EraseForUserAsync(AgentId, default);
        (await _gateDb.GateScanEvents.AsNoTracking().SingleAsync(Xunit.TestContext.Current.CancellationToken)).ScannedByUserId.Should().Be(AgentId);
    }

    [HumansFact]
    public async Task Merge_RepointsStaffLinksToSurvivor()
    {
        StubTicket(matchedUserId: GuestId);
        await RecordAsync(idConfirmed: true, overrideEarly: true, pin: SupervisorPin);
        (await _svc.UndoAdmitAtGateAsync(Barcode, SupervisorPin, AgentId)).Outcome.Should().Be(GateUndoOutcome.Undone);
        var survivor = Guid.NewGuid();

        await _svc.ReassignAsync(AgentId, survivor, actorUserId: Guid.NewGuid(), _clock.GetCurrentInstant(), default);

        var row = await _gateDb.GateScanEvents.AsNoTracking().SingleAsync(Xunit.TestContext.Current.CancellationToken);
        row.ScannedByUserId.Should().Be(survivor);
        row.OverrideByUserId.Should().Be(survivor);
        row.UndoneByUserId.Should().Be(survivor);
    }

    [HumansFact]
    public async Task Retention_PurgesScansBeforeCutoff()
    {
        StubTicket(matchedUserId: GuestId);
        await RecordAsync(idConfirmed: true);

        var removed = await _svc.PurgeScansBeforeAsync(_clock.GetCurrentInstant().Plus(Duration.FromMinutes(1)));

        removed.Should().Be(1);
        SliceCount(await _svc.ContributeForUserAsync(GuestId, default)).Should().Be(0);
    }

    // Tickets issued by Humans' own checkout (the 2027 lane): the gate's row is the only check-in record.

    private const string IssuedBarcode = "H0123456789ABCDEF";

    private readonly EventSettingsInfo _activeEvent = new(Guid.NewGuid(), "Nowhere 2027", 2027, "Europe/Madrid",
        new LocalDate(2026, 3, 5), -10, 5, 8, new Dictionary<int, int>(), null, null);

    private IssuedTicketInfo StubIssued(
        bool valid = true, Guid? eventId = null, Guid? holder = null, Guid? entryUser = null, string label = "Wave 1",
        bool inResaleQueue = false)
    {
        _burn.GetActiveEventSettings().Returns(_activeEvent);
        _tickets.GetTicketOrdersAsync(Arg.Any<CancellationToken>()).Returns(new List<TicketOrderInfo>());
        var ticket = new IssuedTicketInfo(Guid.NewGuid(), eventId ?? _activeEvent.Id, IssuedBarcode, "Ana Ruiz",
            holder, entryUser ?? holder, label, valid, inResaleQueue);
        _issued.FindIssuedTicketAsync(IssuedBarcode, Arg.Any<CancellationToken>()).Returns(ticket);
        return ticket;
    }

    private Task<GateDecisionResult> RecordIssuedAsync() =>
        _svc.RecordDecisionAsync(
            new GateDecisionInput(IssuedBarcode.ToLowerInvariant(), IdConfirmed: true, ChildWithAdult: false, "L1", null, null),
            AgentId);

    [HumansFact]
    public async Task IssuedTicket_ActiveEvent_AdmitsWithOneRecord()
    {
        var ticket = StubIssued(holder: GuestId);

        var eval = await _svc.EvaluateAsync(IssuedBarcode.ToLowerInvariant());
        eval.Outcome.Should().Be(GatePreCheckOutcome.NeedsIdCheck);
        eval.GuestName.Should().Be("Ana Ruiz");
        eval.TicketTypeName.Should().Be("Wave 1");

        var result = await RecordIssuedAsync();

        result.Verdict.Should().Be(GateVerdict.Admitted);
        var row = (await _gateDb.GateScanEvents.AsNoTracking().SingleAsync(Xunit.TestContext.Current.CancellationToken));
        row.CheckoutTicketId.Should().Be(ticket.Id);
        row.TicketAttendeeId.Should().BeNull();
        row.GuestUserId.Should().Be(GuestId);
        await _users.Received(1).RecordParticipationAsync(
            GuestId, 2027, ParticipationStatus.Attended, Arg.Any<Instant?>(), Arg.Any<CancellationToken>());

        (await _svc.EvaluateAsync(IssuedBarcode)).Outcome.Should().Be(GatePreCheckOutcome.Duplicate);
    }

    [HumansTheory]
    [Xunit.InlineData(false, false)]
    [Xunit.InlineData(true, true)]
    public async Task IssuedTicket_VoidOrForAnotherEvent_IsInvalid(bool valid, bool otherEvent)
    {
        StubIssued(valid: valid, eventId: otherEvent ? Guid.NewGuid() : null, holder: GuestId);

        (await _svc.EvaluateAsync(IssuedBarcode)).Outcome.Should().Be(GatePreCheckOutcome.Invalid);
        (await RecordIssuedAsync()).Verdict.Should().Be(GateVerdict.RejectedInvalid);
        (await _gateDb.GateScanEvents.AsNoTracking().SingleAsync(Xunit.TestContext.Current.CancellationToken)).AdmitDedupeKey.Should().BeNull();
    }

    [HumansFact]
    public async Task IssuedChildTicket_BeforeGeneralEntry_UsesAdultsEarlyEntry()
    {
        await _svc.SaveSettingsAsync(new GateSettingsDto(_clock.GetCurrentInstant().Plus(Duration.FromDays(2))));
        StubIssued(holder: null, entryUser: GuestId, label: "Child · Wave 1");
        _earlyEntry.GetForUserAsync(GuestId, Arg.Any<CancellationToken>())
            .Returns(new EarlyEntryRosterRow(GuestId, _clock.GetCurrentInstant().InUtc().Date, new[] { "Build crew" }, false));

        var eval = await _svc.EvaluateAsync(IssuedBarcode);

        eval.Outcome.Should().Be(GatePreCheckOutcome.NeedsIdCheckEarly);
        eval.GuestUserId.Should().BeNull();
        (await RecordIssuedAsync()).Verdict.Should().Be(GateVerdict.AdmittedEarly);
        await _users.DidNotReceive().RecordParticipationAsync(
            Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<ParticipationStatus>(), Arg.Any<Instant?>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task IssuedTicket_StillOnResale_Stops()
    {
        StubIssued(valid: false, holder: GuestId, inResaleQueue: true);

        var eval = await _svc.EvaluateAsync(IssuedBarcode);

        eval.Outcome.Should().Be(GatePreCheckOutcome.Invalid);
        GateScanCardViewModel.FromEvaluation(eval).Reason.Should().Contain("withdraw");
    }

    [HumansFact]
    public async Task Admissions_ReportTheAdmittedCodes_AsTheCallerSpeltThem()
    {
        StubIssued(holder: GuestId);
        await RecordIssuedAsync();
        var reads = new GateAdmissionReads(new GateRepository(_gateDbFactory));

        var admitted = await reads.GetAdmittedAsync(
            [IssuedBarcode.ToLowerInvariant(), "HOTHERCODE", " "], Xunit.TestContext.Current.CancellationToken);

        admitted.Should().BeEquivalentTo([IssuedBarcode.ToLowerInvariant()]);
        (await reads.GetAdmittedAsync([], Xunit.TestContext.Current.CancellationToken)).Should().BeEmpty();
    }

    // Undo a mistaken check-in (G6).

    private CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [HumansFact]
    public async Task UndoAtGate_WrongPin_KeepsAdmit()
    {
        StubIssued(holder: GuestId);
        await RecordIssuedAsync();

        var result = await _svc.UndoAdmitAtGateAsync(IssuedBarcode, "0000", AgentId);

        result.Outcome.Should().Be(GateUndoOutcome.PinRefused);
        result.PinFailure.Should().Be(GateOverrideFailure.InvalidPin);
        (await _gateDb.GateScanEvents.AsNoTracking().SingleAsync(Ct)).AdmitDedupeKey.Should().NotBeNull();
        await _users.DidNotReceive().UndoCheckInAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task UndoAtGate_CorrectPin_FreesBarcode()
    {
        StubIssued(holder: GuestId);
        await RecordIssuedAsync();

        var result = await _svc.UndoAdmitAtGateAsync(IssuedBarcode.ToLowerInvariant(), SupervisorPin, AgentId);

        result.Outcome.Should().Be(GateUndoOutcome.Undone);
        var row = await _gateDb.GateScanEvents.AsNoTracking().SingleAsync(Ct);
        row.AdmitDedupeKey.Should().BeNull();
        row.UndoneByUserId.Should().Be(AgentId);
        row.UndoneAt.Should().Be(_clock.GetCurrentInstant());
        row.UndoReason.Should().Be(GateService.UndoneAtGateReason);
        await _users.Received(1).UndoCheckInAsync(GuestId, 2027, CancellationToken.None);
        await _auditLog.Received(1).LogAsync(GateAuditActions.GateAdmitUndone, nameof(GateScanEvent), row.Id,
            Arg.Any<string>(), AgentId, relatedEntityId: GuestId, relatedEntityType: Arg.Any<string?>());
        (await new GateAdmissionReads(new GateRepository(_gateDbFactory)).GetAdmittedAsync([IssuedBarcode], Ct))
            .Should().BeEmpty();

        (await _svc.EvaluateAsync(IssuedBarcode)).Outcome.Should().Be(GatePreCheckOutcome.NeedsIdCheck);
        (await RecordIssuedAsync()).Verdict.Should().Be(GateVerdict.Admitted);
        (await _svc.UndoAdmitAtGateAsync("H-UNKNOWN", SupervisorPin, AgentId)).Outcome.Should().Be(GateUndoOutcome.NotAdmitted);
    }

    [HumansFact]
    public async Task Undo_SecondTime_IsNotAdmitted()
    {
        StubIssued(holder: GuestId);
        await RecordIssuedAsync();
        var scanId = (await _gateDb.GateScanEvents.AsNoTracking().SingleAsync(Ct)).Id;

        (await _svc.UndoAdmitAsync(scanId, "  ", AgentId)).Outcome.Should().Be(GateUndoOutcome.ReasonRequired);
        (await _svc.UndoAdmitAsync(scanId, new string('x', GateScanEvent.NoteMaxLength + 1), AgentId))
            .Outcome.Should().Be(GateUndoOutcome.ReasonRequired);
        (await _svc.UndoAdmitAsync(scanId, "Scanned the wrong wristband", AgentId)).Outcome.Should().Be(GateUndoOutcome.Undone);
        (await _svc.UndoAdmitAsync(scanId, "Again", AgentId)).Outcome.Should().Be(GateUndoOutcome.NotAdmitted);

        (await _gateDb.GateScanEvents.AsNoTracking().SingleAsync(Ct)).UndoReason.Should().Be("Scanned the wrong wristband");
        await _users.Received(1).UndoCheckInAsync(GuestId, 2027, Arg.Any<CancellationToken>());
        (await _svc.GetArrivalsReportAsync(Ct))!.Arrived.Should().Be(0);
    }

    [HumansTheory]
    [Xunit.InlineData(30, false)]
    [Xunit.InlineData(31, true)]
    public async Task Undo_KeepsArrival_WhenGuestHasAdmitWithin30Days(int daysEarlier, bool reverts)
    {
        StubIssued(holder: GuestId);
        await RecordIssuedAsync();
        _gateDb.GateScanEvents.Add(new GateScanEvent
        {
            Id = Guid.NewGuid(),
            Barcode = "OTHER",
            AdmitDedupeKey = "OTHER",
            Verdict = GateVerdict.Admitted,
            GuestUserId = GuestId,
            ScannedByUserId = AgentId,
            OccurredAt = _clock.GetCurrentInstant() - Duration.FromDays(daysEarlier),
        });
        await _gateDb.SaveChangesAsync(Ct);

        (await _svc.UndoAdmitAtGateAsync(IssuedBarcode, SupervisorPin, AgentId)).Outcome.Should().Be(GateUndoOutcome.Undone);

        await _users.Received(reverts ? 1 : 0).UndoCheckInAsync(GuestId, Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [Xunit.InlineData(false, (int)GateUndoOutcome.Undone)]
    [Xunit.InlineData(true, (int)GateUndoOutcome.VendorCheckedIn)]
    public async Task Undo_TicketTailorAdmit_RefusesWhenVendorHasCheckIn(bool vendorCheckedIn, int outcome)
    {
        var expected = (GateUndoOutcome)outcome;
        var attendeeId = Guid.NewGuid();
        StubTicket(matchedUserId: GuestId, attendeeId: attendeeId);
        StubActiveEvent();
        (await RecordAsync(idConfirmed: true)).Verdict.Should().Be(GateVerdict.Admitted);
        if (vendorCheckedIn)
            StubTicket(matchedUserId: GuestId, attendeeId: attendeeId, checkedInAt: _clock.GetCurrentInstant());

        var result = await _svc.UndoAdmitAtGateAsync(Barcode, SupervisorPin, AgentId);

        result.Outcome.Should().Be(expected);
        (await _gateDb.GateScanEvents.AsNoTracking().SingleAsync(Ct)).IsLiveAdmit.Should().Be(expected != GateUndoOutcome.Undone);
        await _users.Received(expected == GateUndoOutcome.Undone ? 1 : 0)
            .UndoCheckInAsync(GuestId, Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public void UndoCard_MapsEachOutcome()
    {
        GateScanCardViewModel.FromUndo(new GateUndoResult(GateUndoOutcome.Undone), Barcode).Headline.Should().Be("Undone");
        GateScanCardViewModel.FromUndo(new GateUndoResult(GateUndoOutcome.NotAdmitted), Barcode).AllowUndo.Should().BeFalse();
        GateScanCardViewModel.FromUndo(
            new GateUndoResult(GateUndoOutcome.PinRefused, GateOverrideFailure.InvalidPin), Barcode).AllowUndo.Should().BeTrue();
        GateScanCardViewModel.FromUndo(new GateUndoResult(GateUndoOutcome.VendorCheckedIn), Barcode).Reason.Should().Contain("Ticket Tailor");
    }

    // Find a ticket by name (G3).

    private IssuedTicketInfo IssuedFor(string name, Guid? holder = null, Guid? entryUser = null, bool valid = true, string? barcode = null) =>
        new(Guid.NewGuid(), _activeEvent.Id, barcode ?? "H" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant(), name,
            holder, entryUser ?? holder, "Wave 1", valid, InResaleQueue: false);

    private void StubPeople(string query, params Guid[] ids) =>
        _people.SearchUsersAsync(query, PersonSearchFields.Name | PersonSearchFields.LegalName, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ids.Select(id => new HumanSearchResult(id, "x", null, null, null, null, 1)).ToList());

    [HumansFact]
    public async Task Find_TicketName_MatchesAccentCaseBlind()
    {
        StubTicket();
        _burn.GetActiveEventSettings().Returns(_activeEvent);
        _issued.GetIssuedTicketsForEventAsync(_activeEvent.Id, Arg.Any<CancellationToken>())
            .Returns([IssuedFor("José Núñez"), IssuedFor("Jose Void", valid: false)]);
        StubPeople("jose");

        var matches = await _svc.FindTicketsByNameAsync(" jose ");

        matches.Select(m => m.Name).Should().BeEquivalentTo(["José Núñez"]);
        (await _svc.FindTicketsByNameAsync("donovan")).Should().ContainSingle(m => m.Barcode == Barcode);
        (await _svc.FindTicketsByNameAsync("j")).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Find_BurnerOrLegalName_MatchesTicketHolder()
    {
        var entryUser = Guid.NewGuid();
        StubTicket(matchedUserId: GuestId);
        _burn.GetActiveEventSettings().Returns(_activeEvent);
        _issued.GetIssuedTicketsForEventAsync(_activeEvent.Id, Arg.Any<CancellationToken>())
            .Returns([IssuedFor("Ana Ruiz", holder: GuestId), IssuedFor("Kid Ruiz", entryUser: entryUser), IssuedFor("Someone Else")]);
        StubPeople("Sparkle", GuestId, entryUser);

        var matches = await _svc.FindTicketsByNameAsync("Sparkle");

        matches.Select(m => m.Name).Should().BeEquivalentTo(["Jane Donovan", "Ana Ruiz", "Kid Ruiz"]);
    }

    [HumansFact]
    public async Task Find_SearchesEveryMatchingHuman_NotOnlyTheTopFew()
    {
        var crowd = Enumerable.Range(0, 60).Select(_ => Guid.NewGuid()).Append(GuestId).ToList();
        StubTicket();
        _burn.GetActiveEventSettings().Returns(_activeEvent);
        _issued.GetIssuedTicketsForEventAsync(_activeEvent.Id, Arg.Any<CancellationToken>())
            .Returns([IssuedFor("Luna Vidal", holder: GuestId)]);
        _people.SearchUsersAsync("Moon", PersonSearchFields.Name | PersonSearchFields.LegalName, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => crowd.Take(ci.ArgAt<int>(2)).Select(id => new HumanSearchResult(id, "x", null, null, null, null, 1)).ToList());

        (await _svc.FindTicketsByNameAsync("Moon")).Should().ContainSingle(m => m.Name == "Luna Vidal");
    }

    // Arrivals report (G8).

    [HumansFact]
    public async Task Report_LiveScans_CountsPerDay()
    {
        var ticket = StubIssued(holder: GuestId);
        _issued.GetIssuedTicketsForEventAsync(_activeEvent.Id, Arg.Any<CancellationToken>())
            .Returns([ticket, IssuedFor("Spare"), IssuedFor("Gone", valid: false)]);

        await RecordIssuedAsync();
        await RecordIssuedAsync();
        await _svc.RecordDecisionAsync(new GateDecisionInput("UNKNOWN-1", true, false, "L1", null, null), AgentId);

        var report = await _svc.GetArrivalsReportAsync();

        report!.TicketsHeld.Should().Be(2);
        report.Arrived.Should().Be(1);
        var day = report.Days.Should().ContainSingle().Subject;
        (day.Admitted, day.Refused, day.Undone).Should().Be((1, 1, 0));
        report.Arrivals.Should().ContainSingle(a => a.Name == "Ana Ruiz" && a.TicketLabel == "Wave 1");

        await _svc.UndoAdmitAtGateAsync(IssuedBarcode, SupervisorPin, AgentId);
        var after = await _svc.GetArrivalsReportAsync();
        after!.Arrived.Should().Be(0);
        after.Days.Single().Undone.Should().Be(1);
        after.Arrivals.Single().UndoneByUserId.Should().Be(AgentId);
    }

    [HumansFact]
    public async Task Report_WithoutAnActiveEvent_IsNull() =>
        (await _svc.GetArrivalsReportAsync()).Should().BeNull();
}
