using Humans.Auth.Contracts;
using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.EarlyEntry.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Settings.Contracts;
using Humans.Shifts.Contracts;
using Humans.Tickets.Contracts;
using Humans.Base.Constants;
using Humans.Gate.Data;
using Humans.Gate.Domain;
using Humans.Gate.Services;
using Humans.Gate.Services.Stores;
using Humans.Gate.Jobs;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NodaTime.Testing;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Humans.Gate.Tests;

/// <summary>
/// Service-level coverage of <see cref="GateService"/> over a real
/// <see cref="GateRepository"/> (in-memory DB) with stubbed cross-section reads —
/// admit, dedupe, name-mismatch, child-with-adult, and the Early-Entry cutoff
/// branches, including that a client "ID confirmed" never overrides a STOP.
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

    private readonly FakeClock Clock = new(Instant.FromUtc(2026, 3, 1, 12, 0));
    private readonly GateDbContext GateDb;
    private readonly TestDbContextFactory<GateDbContext> GateDbFactory;

    private const string Barcode = "TT-ABC-123";
    private static readonly Guid GuestId = Guid.NewGuid();
    private static readonly Guid AgentId = Guid.NewGuid();

    private readonly ITicketService _tickets = Substitute.For<ITicketService>();
    private readonly IEarlyEntryService _earlyEntry = Substitute.For<IEarlyEntryService>();
    private readonly ISettingsService _burn = Substitute.For<ISettingsService>();
    private readonly IShiftService _shifts = Substitute.For<IShiftService>();
    private readonly IRoleAssignmentService _roles = Substitute.For<IRoleAssignmentService>();
    private readonly IPasswordHasher<GateStaffPin> _pinHasher = new PasswordHasher<GateStaffPin>();
    private readonly IAuditLogService _auditLog = Substitute.For<IAuditLogService>();
    private readonly ITicketWrite _users = Substitute.For<ITicketWrite>();
    private readonly IBackgroundJobClient _jobs = Substitute.For<IBackgroundJobClient>();
    private readonly GateVendorMirrorLedger _ledger = new(new MemoryCache(new MemoryCacheOptions()));
    private readonly IConfiguration _config = new ConfigurationBuilder().AddInMemoryCollection().Build();
    private const string SupervisorPin = "2468";
    private readonly GateService _svc;

    public GateServiceTests()
    {
        _config["Gate:SupervisorPin"] = SupervisorPin;
        GateDb = new GateDbContext(_dbOptions);
        GateDbFactory = new TestDbContextFactory<GateDbContext>(_dbOptions);

        _burn.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns((EventSettingsInfo?)null);
        _earlyEntry.GetForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((EarlyEntryRosterRow?)null);
        _svc = new GateService(new GateRepository(GateDbFactory), _tickets, _earlyEntry, _burn, _shifts, _roles, _users, _pinHasher, _auditLog, _jobs, _ledger, new GatePinThrottle(new MemoryCache(new MemoryCacheOptions()), Clock), _config, NullLogger<GateService>.Instance, Clock);

        // Baseline: a past cutoff, so general entry is open; without one every scan fails safe to
        // AMBER. Seeded sync (the ctor can't await) into the store the repository's factory shares.
        GateDb.GateSettings.Add(new GateSettings
        {
            Id = 1,
            GeneralEntryOpensAt = Clock.GetCurrentInstant().Minus(Duration.FromHours(1)),
            MinorAgeThresholdYears = 16,
        });
        GateDb.SaveChanges();
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
            Price: 100m,
            Status: status,
            MatchedUserId: matchedUserId,
            Barcode: barcode,
            CheckedInAt: checkedInAt);

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
    public async Task Override_InvalidOrUnconfiguredPin_RefusesBeforeAnyScanOrSideEffect(
        string? configured, string? supplied, int failure)
    {
        _config["Gate:SupervisorPin"] = configured;
        StubTicket(matchedUserId: GuestId);

        var result = await RecordAsync(idConfirmed: true, overrideEarly: true, pin: supplied);

        result.OverrideFailure.Should().Be((GateOverrideFailure)failure);
        result.Verdict.Should().Be(GateVerdict.Unresolved);
        GateDb.GateScanEvents.AsNoTracking().Should().BeEmpty();
        await _tickets.DidNotReceive().GetTicketOrdersAsync(Arg.Any<CancellationToken>());
        _jobs.DidNotReceive().Create(Arg.Any<Job>(), Arg.Any<IState>());
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
        GateDb.GateScanEvents.AsNoTracking().Should().BeEmpty();

        Clock.Advance(Duration.FromMinutes(14));
        var stillLocked = await RecordAsync(false, child: true, pin: SupervisorPin);
        stillLocked.OverrideFailure.Should().Be(GateOverrideFailure.LockedOut);
        stillLocked.RetryAfterSeconds.Should().Be(60);

        (await RecordAsync(false)).Verdict.Should().Be(GateVerdict.RejectedNameMismatch);
        Clock.Advance(Duration.FromMinutes(1) + Duration.FromSeconds(1));
        (await RecordAsync(false, child: true, pin: SupervisorPin))
            .Verdict.Should().Be(GateVerdict.AdmittedChildWithAdult);
        var admit = await GateDb.GateScanEvents.AsNoTracking().SingleAsync(s => s.AdmitDedupeKey != null,
            Xunit.TestContext.Current.CancellationToken);
        admit.OverrideByUserId.Should().Be(AgentId);
    }

    [HumansFact]
    public async Task Override_CorrectPinClearsPreviousFailures_ButNeverOverridesInvalidTickets()
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
        GateDb.GateScanEvents.AsNoTracking().Should().OnlyContain(s => s.OverrideByUserId == null);
    }

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
    public async Task Evaluate_VendorCheckedInTimestamp_IsDuplicate()
    {
        StubTicket(checkedInAt: Clock.GetCurrentInstant());

        (await _svc.EvaluateAsync(Barcode)).Outcome.Should().Be(GatePreCheckOutcome.Duplicate);
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

        var first = await RecordAsync(idConfirmed: true);
        first.Verdict.Should().Be(GateVerdict.Admitted);

        var second = await RecordAsync(idConfirmed: true, overrideEarly: true, pin: SupervisorPin);
        second.Verdict.Should().Be(GateVerdict.RejectedDuplicate);
        second.PreviousAdmitByUserId.Should().Be(AgentId);
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task RecordDecision_Admit_AlwaysEnqueuesTheVendorMirror_ButMarksTheLedgerOnlyWhenTheFlagIsOn(bool mirrorEnabled)
    {
        StubTicket();
        _config["Gate:VendorMirrorEnabled"] = mirrorEnabled ? "true" : "false";

        (await RecordAsync(idConfirmed: true)).Verdict.Should().Be(GateVerdict.Admitted);

        // Flag off: the job still no-ops itself, but a ledger mark would hide exactly the row
        // the backfill page exists to recover.
        _jobs.Received(1).Create(
            Arg.Is<Job>(j => j.Type.Equals(typeof(GateVendorCheckInJob)) && string.Equals((string)j.Args[0], "v1", StringComparison.Ordinal)),
            Arg.Any<IState>());
        _ledger.WasSent("v1").Should().Be(mirrorEnabled);
    }

    [HumansFact]
    public async Task RecordDecision_MirrorEnqueueFails_PreservesDurableAdmit()
    {
        StubTicket(matchedUserId: GuestId);
        StubActiveEvent(year: 2026);
        _config["Gate:VendorMirrorEnabled"] = "true";
        _jobs.Create(Arg.Any<Job>(), Arg.Any<IState>())
            .Throws(new InvalidOperationException("Job storage unavailable"));

        var result = await RecordAsync(idConfirmed: true);

        result.Verdict.Should().Be(GateVerdict.Admitted);
        _ledger.WasSent("v1").Should().BeFalse();
        await _users.Received(1).RecordParticipationAsync(
            GuestId, 2026, ParticipationStatus.Attended,
            Clock.GetCurrentInstant(), CancellationToken.None);
        (await RecordAsync(idConfirmed: true)).Verdict.Should().Be(GateVerdict.RejectedDuplicate);
        _jobs.Received(1).Create(Arg.Any<Job>(), Arg.Any<IState>());
    }

    [HumansFact]
    public async Task RecordDecision_Rejected_DoesNotMirrorToTheVendor()
    {
        StubTicket();
        _config["Gate:VendorMirrorEnabled"] = "true";

        (await RecordAsync(idConfirmed: false)).Verdict.Should().Be(GateVerdict.RejectedNameMismatch);

        _jobs.ReceivedCalls().Should().BeEmpty();
        _ledger.WasSent("v1").Should().BeFalse();
    }

    [HumansFact]
    public async Task RecordDecision_IdRejected_IsNameMismatch_AndDoesNotBurnTicket()
    {
        StubTicket();

        var r = await RecordAsync(idConfirmed: false);
        r.Verdict.Should().Be(GateVerdict.RejectedNameMismatch);

        // Ticket not burned: a corrected re-scan can still be admitted.
        (await _svc.EvaluateAsync(Barcode)).Outcome.Should().Be(GatePreCheckOutcome.NeedsIdCheck);
    }

    [HumansFact]
    public async Task RecordDecision_ChildWithAdult_WithSupervisor_Admits()
    {
        StubTicket();

        // Child waivers verify the PIN even without an early-override flag.
        (await RecordAsync(idConfirmed: false, child: true, pin: SupervisorPin))
            .Verdict.Should().Be(GateVerdict.AdmittedChildWithAdult);
    }

    [HumansFact]
    public async Task RecordDecision_ChildWithAdult_WithoutSupervisor_DoesNotAdmit()
    {
        StubTicket();

        var result = await RecordAsync(idConfirmed: false, child: true);
        result.OverrideFailure.Should().Be(GateOverrideFailure.InvalidPin);
        GateDb.GateScanEvents.AsNoTracking().Should().BeEmpty();
    }

    private void StubActiveEvent(int year = 2026) =>
        _burn.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns(BurnFixtures.Burn(
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
            Clock.GetCurrentInstant(), CancellationToken.None);
    }

    [HumansFact]
    public async Task RecordDecision_Admit_ParticipationWriteThrows_StillAdmits()
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
    public async Task RecordDecision_Admit_UnmatchedGuest_DoesNotTouchParticipation()
    {
        // Barcode not matched to a Humans account: there is no user to mark Attended.
        StubTicket(matchedUserId: null);
        StubActiveEvent();

        (await RecordAsync(idConfirmed: true)).Verdict.Should().Be(GateVerdict.Admitted);

        await _users.DidNotReceiveWithAnyArgs()
            .RecordParticipationAsync(default, default, default, default);
    }

    [HumansFact]
    public async Task RecordDecision_Admit_NoActiveEvent_StillAdmits()
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
        await _svc.SaveSettingsAsync(new GateSettingsDto(Instant.MinValue, 16));

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

        (await RecordAsync(idConfirmed: true))
            .Verdict.Should().Be(GateVerdict.RejectedTooEarly);
    }

    [HumansFact]
    public async Task BeforeCutoff_TooEarly_WithSupervisorOverride_AdmitsEarlyOverride()
    {
        StubTicket(matchedUserId: GuestId);
        await _svc.SaveSettingsAsync(new GateSettingsDto(
            Clock.GetCurrentInstant().Plus(Duration.FromHours(6)), 16));

        (await _svc.EvaluateAsync(Barcode)).Outcome.Should().Be(GatePreCheckOutcome.TooEarly);

        // A recorded supervisor override turns the too-early STOP into an attributable early admit.
        (await RecordAsync(idConfirmed: false, overrideEarly: true, pin: SupervisorPin))
            .Verdict.Should().Be(GateVerdict.AdmittedEarlyOverride);
    }

    [HumansFact]
    public async Task Evaluate_TooEarly_CarriesEarlyAndGeneralEntryDates_ForTheReason()
    {
        StubTicket(matchedUserId: GuestId);
        var cutoff = Clock.GetCurrentInstant().Plus(Duration.FromHours(6));
        await _svc.SaveSettingsAsync(new GateSettingsDto(cutoff, 16));
        // Holds a *later-day* Early Entry grant (tomorrow) — the distinguishing too-early sub-case.
        var tomorrow = Clock.GetCurrentInstant().InUtc().Date.PlusDays(1);
        _earlyEntry.GetForUserAsync(GuestId, Arg.Any<CancellationToken>())
            .Returns(new EarlyEntryRosterRow(GuestId, tomorrow, new[] { "Crew" }, false));

        var r = await _svc.EvaluateAsync(Barcode);

        r.Outcome.Should().Be(GatePreCheckOutcome.TooEarly);
        r.EarliestEntryDate.Should().Be(tomorrow);
        r.Today.Should().Be(Clock.GetCurrentInstant().InUtc().Date);
        r.GeneralEntryDate.Should().Be(cutoff.InUtc().Date);
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
    public async Task BeforeCutoff_Unmatched_WithoutOverride_StaysUnresolved()
    {
        StubTicket(matchedUserId: null);
        await _svc.SaveSettingsAsync(new GateSettingsDto(
            Clock.GetCurrentInstant().Plus(Duration.FromHours(6)), 16));

        // Even a client-asserted "ID confirmed" never turns the unconfirmed-EE amber into an admit.
        (await RecordAsync(idConfirmed: true))
            .Verdict.Should().Be(GateVerdict.Unresolved);
    }

    [HumansFact]
    public async Task BeforeCutoff_Unmatched_WithSupervisorOverride_AdmitsEarlyOverride()
    {
        StubTicket(matchedUserId: null);
        await _svc.SaveSettingsAsync(new GateSettingsDto(
            Clock.GetCurrentInstant().Plus(Duration.FromHours(6)), 16));

        // A recorded supervisor override admits an unconfirmed-EE scan, mirroring the too-early case.
        (await RecordAsync(idConfirmed: false, overrideEarly: true, pin: SupervisorPin))
            .Verdict.Should().Be(GateVerdict.AdmittedEarlyOverride);
    }

    [HumansFact]
    public async Task BeforeCutoff_EarlyEntryToday_AdmitsEarly()
    {
        StubTicket(matchedUserId: GuestId);
        await _svc.SaveSettingsAsync(new GateSettingsDto(
            Clock.GetCurrentInstant().Plus(Duration.FromHours(6)), 16));
        _earlyEntry.GetForUserAsync(GuestId, Arg.Any<CancellationToken>())
            .Returns(new EarlyEntryRosterRow(GuestId, Clock.GetCurrentInstant().InUtc().Date, new[] { "Build crew" }, false));

        (await RecordAsync(idConfirmed: true))
            .Verdict.Should().Be(GateVerdict.AdmittedEarly);
    }

    [HumansFact]
    public async Task Leaderboard_TalliesAdmitsAndRejects()
    {
        StubTicket();
        await RecordAsync(idConfirmed: true);   // admit
        await RecordAsync(idConfirmed: true);   // duplicate reject

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
        await RecordAsync(idConfirmed: true);   // GuestUserId = GuestId, ScannedByUserId = AgentId

        var guestExport = await _svc.ContributeForUserAsync(GuestId, default);
        var scannerExport = await _svc.ContributeForUserAsync(AgentId, default);

        guestExport.Single().SectionName.Should().Be(GateService.GateScans);
        SliceCount(guestExport).Should().Be(1);
        SliceCount(scannerExport).Should().Be(1);
        SliceCount(await _svc.ContributeForUserAsync(Guid.NewGuid(), default)).Should().Be(0);
    }

    [HumansFact]
    public async Task Gdpr_ExportOmitsBarcodeAndOtherPeoplesIds()
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
            Clock.GetCurrentInstant().Plus(Duration.FromHours(6)), 16));

        // A device clock claiming a post-cutoff scan cannot admit early: server time decides.
        var r = await _svc.RecordDecisionAsync(
            new GateDecisionInput(Barcode, IdConfirmed: true, ChildWithAdult: false, LaneId: "L1",
                ClientScanAt: Clock.GetCurrentInstant().Plus(Duration.FromHours(12)), Note: null),
            AgentId);

        r.Verdict.Should().Be(GateVerdict.RejectedTooEarly);
    }

    [HumansFact]
    public async Task Merge_ReassignsScansToSurvivor()
    {
        StubTicket(matchedUserId: GuestId);
        await RecordAsync(idConfirmed: true);
        var survivor = Guid.NewGuid();

        await _svc.ReassignAsync(GuestId, survivor, actorUserId: Guid.NewGuid(), Clock.GetCurrentInstant(), default);

        SliceCount(await _svc.ContributeForUserAsync(GuestId, default)).Should().Be(0);
        SliceCount(await _svc.ContributeForUserAsync(survivor, default)).Should().Be(1);
    }

    [HumansFact]
    public async Task Gdpr_EraseClearsGuestAndOverrideLinksAndPin_ButKeepsScannerAttribution()
    {
        var supervisor = AgentId;
        StubTicket(matchedUserId: GuestId);
        await RecordAsync(idConfirmed: true, overrideEarly: true, pin: SupervisorPin);
        await _svc.SetOwnPinAsync(GuestId, "2580");

        // Erase the guest: admission links and their PIN row go.
        await _svc.EraseForUserAsync(GuestId, default);
        var row = await GateDb.GateScanEvents.AsNoTracking().SingleAsync(Xunit.TestContext.Current.CancellationToken);
        row.GuestUserId.Should().BeNull();
        GateDb.GateStaffPins.AsNoTracking().Should().BeEmpty();

        // Erase the override authorizer: that link goes too.
        await _svc.EraseForUserAsync(supervisor, default);
        (await GateDb.GateScanEvents.AsNoTracking().SingleAsync(Xunit.TestContext.Current.CancellationToken)).OverrideByUserId.Should().BeNull();

        // Erase the scanner: ScannedByUserId stays — the Art. 17(3) operational record.
        await _svc.EraseForUserAsync(AgentId, default);
        (await GateDb.GateScanEvents.AsNoTracking().SingleAsync(Xunit.TestContext.Current.CancellationToken)).ScannedByUserId.Should().Be(AgentId);
    }

    [HumansFact]
    public async Task Merge_RepointsScannerAndOverride_AndCarriesThePinToAPinlessSurvivor()
    {
        StubTicket(matchedUserId: GuestId);
        await RecordAsync(idConfirmed: true, overrideEarly: true, pin: SupervisorPin);
        await _svc.AdminSetPinAsync(AgentId, "2580", Guid.NewGuid()); // AdminEnrolled = true
        var survivor = Guid.NewGuid();

        await _svc.ReassignAsync(AgentId, survivor, actorUserId: Guid.NewGuid(), Clock.GetCurrentInstant(), default);

        var row = await GateDb.GateScanEvents.AsNoTracking().SingleAsync(Xunit.TestContext.Current.CancellationToken);
        row.ScannedByUserId.Should().Be(survivor);
        row.OverrideByUserId.Should().Be(survivor);
        var pin = await GateDb.GateStaffPins.AsNoTracking().SingleAsync(Xunit.TestContext.Current.CancellationToken);
        pin.UserId.Should().Be(survivor);
        pin.AdminEnrolled.Should().BeTrue(); // enrolment provenance travels with the PIN
    }

    [HumansFact]
    public async Task Merge_SurvivorWithTheirOwnPin_KeepsIt()
    {
        var mergedAway = Guid.NewGuid();
        var survivor = Guid.NewGuid();
        await _svc.SetOwnPinAsync(mergedAway, "2580");
        await _svc.SetOwnPinAsync(survivor, "1357");

        await _svc.ReassignAsync(mergedAway, survivor, actorUserId: Guid.NewGuid(), Clock.GetCurrentInstant(), default);

        (await GateDb.GateStaffPins.AsNoTracking().SingleAsync(Xunit.TestContext.Current.CancellationToken)).UserId.Should().Be(survivor);
        (await _svc.VerifyPinAsync(survivor, "1357")).Should().BeTrue();
        (await _svc.VerifyPinAsync(survivor, "2580")).Should().BeFalse();
    }

    [HumansFact]
    public async Task Retention_PurgesScansBeforeCutoff()
    {
        StubTicket(matchedUserId: GuestId);
        await RecordAsync(idConfirmed: true);

        var removed = await _svc.PurgeScansBeforeAsync(Clock.GetCurrentInstant().Plus(Duration.FromMinutes(1)));

        removed.Should().Be(1);
        SliceCount(await _svc.ContributeForUserAsync(GuestId, default)).Should().Be(0);
    }

    // Clock is fixed at 2026-03-01T12:00Z; in Europe/Madrid (CET, UTC+1 on that
    // date) that's 13:00 local, so a shift starting 13:00 local == now.
    private static UrgentShiftInfo ShiftAt(
        int dayOffset, LocalTime start, params (Guid Id, string Name, SignupStatus Status)[] signups) =>
        UrgentShiftFixtures.Urgent(
            shift: UrgentShiftFixtures.Shift(dayOffset: dayOffset, startTime: start, maxVolunteers: 5),
            departmentName: "Gate",
            signups: signups.Select(s => new ShiftSignupInfo(s.Id, s.Name, s.Status)).ToList());

    [HumansFact]
    public async Task GetShiftRoster_ReturnsDistinctSignedUpVolunteers_OnShiftsNearNow()
    {
        var teamId = Guid.NewGuid();
        Guid alice = Guid.NewGuid(), bob = Guid.NewGuid(), carol = Guid.NewGuid(),
             dave = Guid.NewGuid(), eve = Guid.NewGuid();

        _burn.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns(BurnFixtures.Burn(
            eventName: "Test",
            gateOpeningDate: new LocalDate(2026, 3, 1)));
        _shifts.GetBrowseShiftsAsync(Arg.Any<ShiftBrowseQuery>()).Returns(new List<UrgentShiftInfo>
        {
            // Starts at now: Eve is Refused (excluded), Alice + Bob signed up.
            ShiftAt(0, new LocalTime(13, 0),
                (alice, "Alice", SignupStatus.Confirmed),
                (bob, "Bob", SignupStatus.Confirmed),
                (eve, "Eve", SignupStatus.Refused)),
            // Starts now-1h (in window): Alice again (de-duped), Carol pending (still "signed up").
            ShiftAt(0, new LocalTime(12, 0),
                (alice, "Alice", SignupStatus.Confirmed),
                (carol, "Carol", SignupStatus.Pending)),
            // Starts now+5h (outside ±2h): Dave excluded.
            ShiftAt(0, new LocalTime(18, 0),
                (dave, "Dave", SignupStatus.Confirmed)),
        });

        var roster = await _svc.GetShiftRosterAsync(teamId);

        roster.Select(r => r.DisplayName).Should().Equal("Alice", "Bob", "Carol");
    }

    [HumansFact]
    public async Task GetShiftRoster_NoActiveEvent_ReturnsEmpty()
    {
        _burn.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns((EventSettingsInfo?)null);

        (await _svc.GetShiftRosterAsync(Guid.NewGuid())).Should().BeEmpty();
    }

    [HumansFact]
    public async Task SetOwnPin_NonSupervisor_SetsAndVerifies()
    {
        var user = Guid.NewGuid();

        (await _svc.SetOwnPinAsync(user, "2580")).Should().Be(GatePinSetResult.Ok);
        (await _svc.VerifyPinAsync(user, "2580")).Should().BeTrue();
        (await _svc.VerifyPinAsync(user, "1357")).Should().BeFalse();
    }

    [HumansFact]
    public async Task PinSetAndReset_AreAudited_WithTheActingUser()
    {
        var user = Guid.NewGuid();
        var admin = Guid.NewGuid();

        await _svc.SetOwnPinAsync(user, "2580");                 // self-enrol: actor == the staffer
        await _auditLog.Received(1).LogAsync(AuditAction.GateStaffPinSet, nameof(GateStaffPin), user,
            Arg.Any<string>(), user);

        await _svc.AdminSetPinAsync(user, "1357", admin);        // admin set: actor == the admin
        await _auditLog.Received(1).LogAsync(AuditAction.GateStaffPinSet, nameof(GateStaffPin), user,
            Arg.Any<string>(), admin);

        await _svc.ClearPinAsync(user, admin);                   // admin reset: actor == the admin
        await _auditLog.Received(1).LogAsync(AuditAction.GateStaffPinReset, nameof(GateStaffPin), user,
            Arg.Any<string>(), admin);
    }

    [HumansFact]
    public async Task SetOwnPin_RejectsTrivialOrMalformedPins()
    {
        var user = Guid.NewGuid();
        foreach (var weak in new[] { "0000", "1111", "1234", "4321", "12", "12a4", "" })
            (await _svc.SetOwnPinAsync(user, weak)).Should().Be(GatePinSetResult.InvalidPin);
    }

    [HumansFact]
    public async Task SetOwnPin_Supervisor_SelfSetsClaimPin_ButItCannotOverride()
    {
        var sup = Guid.NewGuid();
        _roles.HasActiveRoleAsync(sup, RoleNames.TicketAdmin, Arg.Any<CancellationToken>()).Returns(true);

        // A supervisor may self-enrol a CLAIM PIN (attribution — everyone can)...
        (await _svc.SetOwnPinAsync(sup, "2580")).Should().Be(GatePinSetResult.Ok);
        (await _svc.VerifyPinAsync(sup, "2580")).Should().BeTrue();          // can claim the scanner
        // ...but a self-set PIN never carries override authority (AdminEnrolled = false).
        (await _svc.AuthorizeOverrideAsync(sup, "2580")).Should().BeFalse();

        // Admin enrolment (AdminEnrolled = true) is the only thing that confers override.
        (await _svc.AdminSetPinAsync(sup, "1357", Guid.NewGuid())).Should().BeTrue();
        (await _svc.AuthorizeOverrideAsync(sup, "1357")).Should().BeTrue();
    }

    [HumansFact]
    public async Task AuthorizeOverride_RequiresEnrolledSupervisorAndCorrectPin()
    {
        var sup = Guid.NewGuid();
        _roles.HasActiveRoleAsync(sup, RoleNames.Board, Arg.Any<CancellationToken>()).Returns(true);
        await _svc.AdminSetPinAsync(sup, "2580", Guid.NewGuid());

        (await _svc.AuthorizeOverrideAsync(sup, "2580")).Should().BeTrue();
        (await _svc.AuthorizeOverrideAsync(sup, "1357")).Should().BeFalse();          // wrong PIN

        var staff = Guid.NewGuid();                                                    // enrolled, not a supervisor
        await _svc.SetOwnPinAsync(staff, "2580");
        (await _svc.AuthorizeOverrideAsync(staff, "2580")).Should().BeFalse();

        var unenrolledSup = Guid.NewGuid();                                            // supervisor, no PIN
        _roles.HasActiveRoleAsync(unenrolledSup, RoleNames.Admin, Arg.Any<CancellationToken>()).Returns(true);
        (await _svc.AuthorizeOverrideAsync(unenrolledSup, "2580")).Should().BeFalse();

        var selfSetSup = Guid.NewGuid();                                               // supervisor who SELF-set a claim PIN
        _roles.HasActiveRoleAsync(selfSetSup, RoleNames.Admin, Arg.Any<CancellationToken>()).Returns(true);
        await _svc.SetOwnPinAsync(selfSetSup, "2580");                                 // AdminEnrolled = false
        (await _svc.AuthorizeOverrideAsync(selfSetSup, "2580")).Should().BeFalse();    // correct PIN + role, but not admin-enrolled
    }

    private async Task SeedBackfillAsync()
    {
        var first = StubTicket(barcode: "TIX-A", vendorTicketId: "vt-A");
        await _svc.RecordDecisionAsync(new GateDecisionInput("TIX-A", true, false, null, null, null), AgentId);
        var second = StubTicket(barcode: "TIX-B", vendorTicketId: "vt-B");
        await _svc.RecordDecisionAsync(new GateDecisionInput("TIX-B", true, false, null, null, null), AgentId);
        _tickets.GetTicketOrdersAsync(Arg.Any<CancellationToken>()).Returns(new List<TicketOrderInfo> { first, second });
        _jobs.ClearReceivedCalls();
        _config["Gate:VendorMirrorEnabled"] = "true";
    }

    private Job[] BackfillJobs() => _jobs.ReceivedCalls()
        .Where(c => string.Equals(c.GetMethodInfo().Name, nameof(IBackgroundJobClient.Create), StringComparison.Ordinal))
        .Select(c => (Job)c.GetArguments()[0]!).ToArray();

    [HumansFact]
    public async Task VendorBackfill_TestThenBulk_ClaimsEachTicketOnce_WithOriginalAdmitTime()
    {
        await SeedBackfillAsync();
        var admittedAt = Clock.GetCurrentInstant();
        Clock.Advance(Duration.FromHours(1));

        var one = await _svc.EnqueueVendorCheckInBackfillAsync("vt-A");
        one.Enqueued.Should().ContainSingle().Which.VendorTicketId.Should().Be("vt-A");
        _ledger.WasSent("vt-A").Should().BeTrue();
        _ledger.WasSent("vt-B").Should().BeFalse();
        var snapshot = await _svc.GetVendorCheckInBackfillAsync();
        snapshot.Pending.Should().ContainSingle().Which.VendorTicketId.Should().Be("vt-B");
        snapshot.SentAwaitingSync.Should().ContainSingle().Which.VendorTicketId.Should().Be("vt-A");
        snapshot.MirrorEnabled.Should().BeTrue();

        (await _svc.EnqueueVendorCheckInBackfillAsync("vt-A")).Enqueued.Should().BeEmpty();
        (await _svc.EnqueueVendorCheckInBackfillAsync(null)).Enqueued
            .Should().ContainSingle().Which.VendorTicketId.Should().Be("vt-B");
        (await _svc.EnqueueVendorCheckInBackfillAsync(null)).Enqueued.Should().BeEmpty();
        var jobs = BackfillJobs();
        jobs.Select(j => (string)j.Args[0]).Should().Equal("vt-A", "vt-B");
        foreach (var job in jobs)
        {
            job.Method.Name.Should().Be(nameof(GateVendorCheckInJob.ExecuteBackfillAsync));
            job.Args[1].Should().Be(admittedAt.ToUnixTimeSeconds());
            job.Args[2].Should().Be(CancellationToken.None);
        }
    }

    [HumansTheory]
    [Xunit.InlineData("")]
    [Xunit.InlineData("unknown")]
    public async Task VendorBackfill_UnknownTicket_EnqueuesNothing(string vendorTicketId)
    {
        await SeedBackfillAsync();

        (await _svc.EnqueueVendorCheckInBackfillAsync(vendorTicketId)).Enqueued.Should().BeEmpty();

        BackfillJobs().Should().BeEmpty();
        _ledger.WasSent("vt-A").Should().BeFalse();
        _ledger.WasSent("vt-B").Should().BeFalse();
    }

    [HumansFact]
    public async Task VendorBackfill_Disabled_NeitherReadsNorClaimsNorEnqueues()
    {
        await SeedBackfillAsync();
        _config["Gate:VendorMirrorEnabled"] = "false";
        _tickets.ClearReceivedCalls();

        foreach (var id in new string?[] { "vt-A", null })
        {
            var result = await _svc.EnqueueVendorCheckInBackfillAsync(id);
            result.MirrorEnabled.Should().BeFalse();
            result.Enqueued.Should().BeEmpty();
        }

        await _tickets.DidNotReceive().GetTicketOrdersAsync(Arg.Any<CancellationToken>());
        BackfillJobs().Should().BeEmpty();
        _ledger.WasSent("vt-A").Should().BeFalse();
        _ledger.WasSent("vt-B").Should().BeFalse();
    }

    [HumansFact]
    public async Task VendorBackfill_RechecksVendorStatusBeforeEnqueueing()
    {
        var attendeeId = Guid.NewGuid();
        StubTicket(attendeeId: attendeeId);
        await RecordAsync(idConfirmed: true);
        (await _svc.GetVendorCheckInBackfillAsync()).Pending.Should().ContainSingle();
        StubTicket(attendeeId: attendeeId, checkedInAt: Clock.GetCurrentInstant());
        _config["Gate:VendorMirrorEnabled"] = "true";
        _jobs.ClearReceivedCalls();

        (await _svc.EnqueueVendorCheckInBackfillAsync("v1")).Enqueued.Should().BeEmpty();

        BackfillJobs().Should().BeEmpty();
        _ledger.WasSent("v1").Should().BeFalse();
    }

    [HumansFact]
    public void VendorMirrorLedger_ClaimsAnIdExactlyOnce()
    {
        _ledger.TryMarkSent("vt-X").Should().BeTrue();
        _ledger.TryMarkSent("vt-X").Should().BeFalse();
        _ledger.WasSent("vt-X").Should().BeTrue();
    }

    [HumansFact]
    public async Task VendorBackfill_AdmitNotCheckedInAtVendor_IsPending()
    {
        StubTicket();
        await RecordAsync(idConfirmed: true);

        var snapshot = await _svc.GetVendorCheckInBackfillAsync();

        snapshot.AdmitCount.Should().Be(1);
        snapshot.AlreadyCheckedInCount.Should().Be(0);
        snapshot.Unmirrorable.Should().BeEmpty();
        var row = snapshot.Pending.Should().ContainSingle().Subject;
        row.VendorTicketId.Should().Be("v1");
        row.AttendeeName.Should().Be("Jane Donovan");
    }

    [HumansFact]
    public async Task VendorBackfill_VendorAlreadyCheckedIn_IsNotPending()
    {
        var attendeeId = Guid.NewGuid();
        StubTicket(attendeeId: attendeeId);
        await RecordAsync(idConfirmed: true);
        // The next /check_ins sync stamps CheckedInAt — the issued ticket's Status stays
        // Valid (vendor semantics), so CheckedInAt alone must drop the row out of pending.
        StubTicket(attendeeId: attendeeId, checkedInAt: Clock.GetCurrentInstant());

        var snapshot = await _svc.GetVendorCheckInBackfillAsync();

        snapshot.AdmitCount.Should().Be(1);
        snapshot.AlreadyCheckedInCount.Should().Be(1);
        snapshot.Pending.Should().BeEmpty();
        snapshot.Unmirrorable.Should().BeEmpty();
    }

    [HumansFact]
    public async Task VendorBackfill_CheckedInStatusWithoutTimestamp_AlsoNotPending()
    {
        var attendeeId = Guid.NewGuid();
        StubTicket(attendeeId: attendeeId);
        await RecordAsync(idConfirmed: true);
        StubTicket(status: TicketAttendeeStatus.CheckedIn, attendeeId: attendeeId);

        var snapshot = await _svc.GetVendorCheckInBackfillAsync();

        snapshot.AlreadyCheckedInCount.Should().Be(1);
        snapshot.Pending.Should().BeEmpty();
    }

    [HumansFact]
    public async Task VendorBackfill_AttendeeNoLongerInOrders_IsUnmirrorable()
    {
        StubTicket();
        await RecordAsync(idConfirmed: true);
        _tickets.GetTicketOrdersAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TicketOrderInfo>());

        var snapshot = await _svc.GetVendorCheckInBackfillAsync();

        snapshot.Pending.Should().BeEmpty();
        var row = snapshot.Unmirrorable.Should().ContainSingle().Subject;
        row.VendorTicketId.Should().BeNull();
    }

    [HumansFact]
    public async Task VendorBackfill_IgnoresAdmitsOlderThanTheWindow()
    {
        StubTicket();
        await RecordAsync(idConfirmed: true);
        // Prior-edition admits (retention is 365 days) must not pollute the diff.
        Clock.Advance(Duration.FromDays(31));

        var snapshot = await _svc.GetVendorCheckInBackfillAsync();

        snapshot.AdmitCount.Should().Be(0);
        snapshot.Pending.Should().BeEmpty();
        snapshot.Unmirrorable.Should().BeEmpty();
    }

    [HumansFact]
    public async Task VendorBackfill_RejectedScansAreNotCounted()
    {
        StubTicket();
        await RecordAsync(idConfirmed: false); // ID says no → rejected, nothing to mirror

        var snapshot = await _svc.GetVendorCheckInBackfillAsync();

        snapshot.AdmitCount.Should().Be(0);
        snapshot.Pending.Should().BeEmpty();
        snapshot.Unmirrorable.Should().BeEmpty();
    }
}
