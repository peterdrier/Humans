using AwesomeAssertions;
using Humans.Application;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.EarlyEntry;
using Humans.Application.Interfaces.Gate;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Services.Gate;
using Humans.Domain.Constants;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Repositories.Gate;
using Microsoft.AspNetCore.Identity;
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
    private readonly IShiftManagementService _shifts = Substitute.For<IShiftManagementService>();
    private readonly IRoleAssignmentService _roles = Substitute.For<IRoleAssignmentService>();
    private readonly IPasswordHasher<GateStaffPin> _pinHasher = new PasswordHasher<GateStaffPin>();
    private readonly IAuditLogService _auditLog = Substitute.For<IAuditLogService>();
    private readonly GateService _svc;

    public GateServiceTests()
    {
        _burn.GetActiveAsync(Arg.Any<CancellationToken>()).Returns((BurnSettingsInfo?)null);
        _earlyEntry.GetForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((UserEarlyEntry?)null);
        _svc = new GateService(new GateRepository(DbFactory), _tickets, _earlyEntry, _burn, _shifts, _roles, _pinHasher, _auditLog, Clock);

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

    private Task<GateDecisionResult> Record(bool idConfirmed, bool child = false, Guid? overrideBy = null) =>
        _svc.RecordDecisionAsync(
            new GateDecisionInput(Barcode, idConfirmed, child, LaneId: "L1", ClientScanAt: null, Note: null,
                OverrideByUserId: overrideBy),
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
    public async Task RecordDecision_ChildWithAdult_WithSupervisor_Admits()
    {
        StubTicket();

        // The child-without-ID waiver is now a supervisor override — it admits only when an
        // authorizing supervisor was recorded (the controller sets this after AuthorizeOverrideAsync).
        (await Record(idConfirmed: false, child: true, overrideBy: Guid.NewGuid()))
            .Verdict.Should().Be(GateVerdict.AdmittedChildWithAdult);
    }

    [HumansFact]
    public async Task RecordDecision_ChildWithAdult_WithoutSupervisor_DoesNotAdmit()
    {
        StubTicket();

        // No authorizing supervisor → the waiver is not granted (a forged child flag can't admit).
        (await Record(idConfirmed: false, child: true))
            .Verdict.Should().Be(GateVerdict.RejectedNameMismatch);
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
    public async Task BeforeCutoff_TooEarly_WithSupervisorOverride_AdmitsEarlyOverride()
    {
        StubTicket(matchedUserId: GuestId);
        await _svc.SaveSettingsAsync(new GateSettingsDto(
            Clock.GetCurrentInstant().Plus(Duration.FromHours(6)), 16));

        (await _svc.EvaluateAsync(Barcode)).Outcome.Should().Be(GatePreCheckOutcome.TooEarly);

        // A recorded supervisor override turns the too-early STOP into an attributable early admit.
        (await Record(idConfirmed: false, overrideBy: Guid.NewGuid()))
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
            .Returns(new UserEarlyEntry(tomorrow, new[] { "Crew" }));

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
        (await Record(idConfirmed: true))
            .Verdict.Should().Be(GateVerdict.Unresolved);
    }

    [HumansFact]
    public async Task BeforeCutoff_Unmatched_WithSupervisorOverride_AdmitsEarlyOverride()
    {
        StubTicket(matchedUserId: null);
        await _svc.SaveSettingsAsync(new GateSettingsDto(
            Clock.GetCurrentInstant().Plus(Duration.FromHours(6)), 16));

        // A recorded supervisor override admits an unconfirmed-EE scan, mirroring the too-early case.
        (await Record(idConfirmed: false, overrideBy: Guid.NewGuid()))
            .Verdict.Should().Be(GateVerdict.AdmittedEarlyOverride);
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

    // ── Shift roster (claim-screen pre-fill) ─────────────────────────────────
    // Clock is fixed at 2026-03-01T12:00Z; in Europe/Madrid (CET, UTC+1 on that
    // date) that's 13:00 local, so a shift starting 13:00 local == now.
    private static UrgentShift ShiftAt(
        int dayOffset, LocalTime start, params (Guid Id, string Name, SignupStatus Status)[] signups) =>
        new(new Shift
        {
            Id = Guid.NewGuid(),
            RotaId = Guid.NewGuid(),
            DayOffset = dayOffset,
            StartTime = start,
            Duration = Duration.FromHours(4),
            MinVolunteers = 1,
            MaxVolunteers = 5,
        },
            UrgencyScore: 0, ConfirmedCount: 0, RemainingSlots: 0, DepartmentName: "Gate",
            Signups: signups.Select(s => (s.Id, s.Name, s.Status)).ToList());

    [HumansFact]
    public async Task GetShiftRoster_ReturnsDistinctSignedUpVolunteers_OnShiftsNearNow()
    {
        var teamId = Guid.NewGuid();
        Guid alice = Guid.NewGuid(), bob = Guid.NewGuid(), carol = Guid.NewGuid(),
             dave = Guid.NewGuid(), eve = Guid.NewGuid();

        _shifts.GetActiveAsync().Returns(new EventSettings
        {
            Id = Guid.NewGuid(),
            EventName = "Test",
            TimeZoneId = "Europe/Madrid",
            GateOpeningDate = new LocalDate(2026, 3, 1),
        });
        _shifts.GetBrowseShiftsAsync(Arg.Any<ShiftBrowseQuery>()).Returns(new List<UrgentShift>
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
        _shifts.GetActiveAsync().Returns((EventSettings?)null);

        (await _svc.GetShiftRosterAsync(Guid.NewGuid())).Should().BeEmpty();
    }

    // ── Personal device PINs ─────────────────────────────────────────────────
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

        // A supervisor may now self-enrol a CLAIM PIN (attribution — everyone can)...
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

    [HumansFact]
    public async Task GetEnrolledSupervisorIds_ReturnsOnlySupervisorsWithAnAdminEnrolledPin()
    {
        Guid enrolled = Guid.NewGuid(), selfSetOnly = Guid.NewGuid();
        _roles.GetActiveUserIdsInRoleAsync(RoleNames.Board, Arg.Any<CancellationToken>())
            .Returns(new[] { enrolled });
        _roles.GetActiveUserIdsInRoleAsync(RoleNames.Admin, Arg.Any<CancellationToken>())
            .Returns(new[] { selfSetOnly });
        _roles.GetActiveUserIdsInRoleAsync(RoleNames.TicketAdmin, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Guid>());
        await _svc.AdminSetPinAsync(enrolled, "2580", Guid.NewGuid());   // admin-enrolled → override-capable
        await _svc.SetOwnPinAsync(selfSetOnly, "1357");                  // self-set claim PIN → NOT override-capable

        var ids = await _svc.GetEnrolledSupervisorIdsAsync();

        // Only the admin-enrolled supervisor is offered in the override picker.
        ids.Should().ContainSingle().Which.Should().Be(enrolled);
    }
}
