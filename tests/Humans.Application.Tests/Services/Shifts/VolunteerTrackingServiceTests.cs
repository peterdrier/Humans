using AwesomeAssertions;
using Humans.Application.DTOs;
using Humans.Application.DTOs.VolunteerTrackingExport;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Shifts;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Application.Tests.Services.Shifts;

public class VolunteerTrackingServiceTests
{
    // Fixed test "now": 2026-06-15 10:00 UTC. GateOpeningDate defaults to
    // 2026-06-16 (Madrid), so by default todayOffset = -1.
    private static readonly Instant TestNow = Instant.FromUtc(2026, 6, 15, 10, 0);
    private static readonly LocalDate DefaultGateOpening = new(2026, 6, 16);

    [HumansFact]
    public async Task GetTrackingDataAsync_returns_empty_when_no_active_event()
    {
        var sut = BuildSut(activeEvent: null);

        var result = await sut.GetTrackingDataAsync();

        result.HasActiveEvent.Should().BeFalse();
        result.MainCohort.Should().BeEmpty();
        result.UnbookedCohort.Should().BeEmpty();
    }

    [HumansFact]
    public async Task MainCohort_single_volunteer_fully_covered_has_zero_gaps()
    {
        var es = MakeEvent(buildStartOffset: -5);
        var userId = Guid.NewGuid();
        var signups = new List<EligibleBuildSignup>
        {
            new(userId, -5, SignupStatus.Confirmed, "Cleanup"),
            new(userId, -4, SignupStatus.Confirmed, "Cleanup"),
            new(userId, -3, SignupStatus.Confirmed, "Cleanup"),
            new(userId, -2, SignupStatus.Confirmed, "Cleanup"),
            new(userId, -1, SignupStatus.Confirmed, "Cleanup"),
        };
        var participations = new[] { Participation(userId, ParticipationStatus.Ticketed, es.Year) };

        var sut = BuildSut(es, signups: signups, participations: participations);

        var result = await sut.GetTrackingDataAsync();

        result.HasActiveEvent.Should().BeTrue();
        result.MainCohort.Should().HaveCount(1);
        var row = result.MainCohort[0];
        row.UserId.Should().Be(userId);
        row.GapCount.Should().Be(0);
        row.FirstSignupDay.Should().Be(-5);
        row.LastEligibleSignupOffset.Should().Be(-1);
        row.Cells.Should().HaveCount(5);
        row.Cells.Single(c => c.DayOffset == -5).State.Should().Be(VolunteerCellState.Confirmed);
        row.Cells.Single(c => c.DayOffset == -4).State.Should().Be(VolunteerCellState.Confirmed);
        row.Cells.Single(c => c.DayOffset == -3).State.Should().Be(VolunteerCellState.Confirmed);
        row.Cells.Single(c => c.DayOffset == -2).State.Should().Be(VolunteerCellState.Confirmed);
        row.Cells.Single(c => c.DayOffset == -1).State.Should().Be(VolunteerCellState.Confirmed);
    }

    [HumansFact]
    public async Task MainCohort_mid_window_gap_renders_red_cell()
    {
        var es = MakeEvent(buildStartOffset: -5);
        var userId = Guid.NewGuid();
        // Signups at -5, -4, -2, -1: missing -3.
        var signups = new List<EligibleBuildSignup>
        {
            new(userId, -5, SignupStatus.Confirmed, "Cleanup"),
            new(userId, -4, SignupStatus.Confirmed, "Cleanup"),
            new(userId, -2, SignupStatus.Confirmed, "Cleanup"),
            new(userId, -1, SignupStatus.Confirmed, "Cleanup"),
        };
        var participations = new[] { Participation(userId, ParticipationStatus.Ticketed, es.Year) };

        var sut = BuildSut(es, signups: signups, participations: participations);

        var result = await sut.GetTrackingDataAsync();

        var row = result.MainCohort.Single();
        row.GapCount.Should().Be(1);
        row.Cells.Single(c => c.DayOffset == -3).State.Should().Be(VolunteerCellState.Gap);
        row.Cells.Single(c => c.DayOffset == -5).State.Should().Be(VolunteerCellState.Confirmed);
        row.Cells.Single(c => c.DayOffset == -4).State.Should().Be(VolunteerCellState.Confirmed);
        row.Cells.Single(c => c.DayOffset == -2).State.Should().Be(VolunteerCellState.Confirmed);
        row.Cells.Single(c => c.DayOffset == -1).State.Should().Be(VolunteerCellState.Confirmed);
    }

    [HumansFact]
    public async Task MainCohort_NotAttending_volunteer_excluded()
    {
        var es = MakeEvent(buildStartOffset: -5);
        var userId = Guid.NewGuid();
        var signups = new List<EligibleBuildSignup>
        {
            new(userId, -5, SignupStatus.Confirmed, "Cleanup"),
        };
        var participations = new[] { Participation(userId, ParticipationStatus.NotAttending, es.Year) };

        var sut = BuildSut(es, signups: signups, participations: participations);

        var result = await sut.GetTrackingDataAsync();

        result.MainCohort.Should().BeEmpty();
    }

    [HumansFact]
    public async Task MainCohort_pending_signup_renders_pending_not_gap()
    {
        var es = MakeEvent(buildStartOffset: -5);
        var userId = Guid.NewGuid();
        var signups = new List<EligibleBuildSignup>
        {
            new(userId, -5, SignupStatus.Confirmed, "Cleanup"),
            new(userId, -4, SignupStatus.Confirmed, "Cleanup"),
            new(userId, -3, SignupStatus.Pending, "Cleanup"),
            new(userId, -2, SignupStatus.Confirmed, "Cleanup"),
            new(userId, -1, SignupStatus.Confirmed, "Cleanup"),
        };
        var participations = new[] { Participation(userId, ParticipationStatus.Ticketed, es.Year) };

        var sut = BuildSut(es, signups: signups, participations: participations);

        var result = await sut.GetTrackingDataAsync();

        var row = result.MainCohort.Single();
        row.GapCount.Should().Be(0);
        row.Cells.Single(c => c.DayOffset == -3).State.Should().Be(VolunteerCellState.Pending);
    }

    [HumansFact]
    public async Task MainCohort_camp_setup_cuts_active_window()
    {
        var es = MakeEvent(buildStartOffset: -5);
        var userId = Guid.NewGuid();
        var signups = new List<EligibleBuildSignup>
        {
            new(userId, -5, SignupStatus.Confirmed, "Cleanup"),
            new(userId, -4, SignupStatus.Confirmed, "Cleanup"),
        };
        var participations = new[] { Participation(userId, ParticipationStatus.Ticketed, es.Year) };
        var bs = new VolunteerBuildStatus
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            EventSettingsId = es.Id,
            BarrioSetupStartDate = es.GateOpeningDate.PlusDays(-3), // setupOffset = -3
        };

        var sut = BuildSut(es, signups: signups, participations: participations, buildStatuses: [bs]);

        var result = await sut.GetTrackingDataAsync();

        var row = result.MainCohort.Single();
        row.GapCount.Should().Be(0);
        row.Cells.Single(c => c.DayOffset == -5).State.Should().Be(VolunteerCellState.Confirmed);
        row.Cells.Single(c => c.DayOffset == -4).State.Should().Be(VolunteerCellState.Confirmed);
        row.Cells.Single(c => c.DayOffset == -3).State.Should().Be(VolunteerCellState.CampSetup);
        row.Cells.Single(c => c.DayOffset == -2).State.Should().Be(VolunteerCellState.CampSetup);
        row.Cells.Single(c => c.DayOffset == -1).State.Should().Be(VolunteerCellState.CampSetup);
    }

    [HumansFact]
    public async Task MainCohort_future_unfilled_day_renders_as_gap_for_planning()
    {
        // Today (offset -1 in this fixture) is one day before gate-open. Volunteer
        // has confirmed -5 only; -4..-1 are all unfilled. The cap on lastExpectedDay
        // used to render -1 as "Expected" (today is not in the past), but coordinators
        // need to see future unfilled commitments as gaps so they can voluntell
        // ahead of time. Locks in spec docs/features/47-volunteer-tracking.md step 3.
        var es = MakeEvent(buildStartOffset: -5);
        var userId = Guid.NewGuid();
        var signups = new List<EligibleBuildSignup>
        {
            new(userId, -5, SignupStatus.Confirmed, "Cleanup"),
        };
        var participations = new[] { Participation(userId, ParticipationStatus.Ticketed, es.Year) };

        var sut = BuildSut(es, signups: signups, participations: participations);

        var result = await sut.GetTrackingDataAsync();

        var row = result.MainCohort.Single();
        row.GapCount.Should().Be(4); // -4, -3, -2, -1 are all gaps now.
        row.Cells.Single(c => c.DayOffset == -1).State.Should().Be(VolunteerCellState.Gap);
        row.Cells.Single(c => c.DayOffset == -4).State.Should().Be(VolunteerCellState.Gap);
    }

    [HumansFact]
    public async Task UnbookedCohort_volunteer_with_availability_no_signups_appears()
    {
        var es = MakeEvent(buildStartOffset: -5);
        var userId = Guid.NewGuid();
        var participations = new[] { Participation(userId, ParticipationStatus.Ticketed, es.Year) };
        var availability = new[] { Availability(userId, es.Id, [-5, -4, -3]) };

        var sut = BuildSut(es, participations: participations, availabilities: availability);

        var result = await sut.GetTrackingDataAsync();

        result.MainCohort.Should().BeEmpty();
        result.UnbookedCohort.Should().HaveCount(1);
        var row = result.UnbookedCohort[0];
        row.UserId.Should().Be(userId);
        row.UnbookedCount.Should().Be(3);
        row.FirstAvailableDay.Should().Be(-5);
        row.Cells.Single(c => c.DayOffset == -5).State.Should().Be(VolunteerCellState.AvailableUnbooked);
        row.Cells.Single(c => c.DayOffset == -4).State.Should().Be(VolunteerCellState.AvailableUnbooked);
        row.Cells.Single(c => c.DayOffset == -3).State.Should().Be(VolunteerCellState.AvailableUnbooked);
        // -2, -1: not in availability so NotAvailable.
        row.Cells.Single(c => c.DayOffset == -2).State.Should().Be(VolunteerCellState.NotAvailable);
        row.Cells.Single(c => c.DayOffset == -1).State.Should().Be(VolunteerCellState.NotAvailable);
    }

    [HumansFact]
    public async Task UnbookedCohort_volunteer_with_first_signup_moves_to_main_cohort()
    {
        var es = MakeEvent(buildStartOffset: -5);
        var userId = Guid.NewGuid();
        var participations = new[] { Participation(userId, ParticipationStatus.Ticketed, es.Year) };
        var availability = new[] { Availability(userId, es.Id, [-5, -4, -3]) };
        var signups = new List<EligibleBuildSignup>
        {
            new(userId, -3, SignupStatus.Confirmed, "Cleanup"),
        };

        var sut = BuildSut(es, signups: signups, participations: participations, availabilities: availability);

        var result = await sut.GetTrackingDataAsync();

        result.MainCohort.Should().HaveCount(1);
        result.MainCohort[0].UserId.Should().Be(userId);
        result.UnbookedCohort.Should().BeEmpty();
    }

    [HumansFact]
    public async Task UnbookedCohort_NotAttending_excluded()
    {
        var es = MakeEvent(buildStartOffset: -5);
        var userId = Guid.NewGuid();
        var participations = new[] { Participation(userId, ParticipationStatus.NotAttending, es.Year) };
        var availability = new[] { Availability(userId, es.Id, [-5, -4, -3]) };

        var sut = BuildSut(es, participations: participations, availabilities: availability);

        var result = await sut.GetTrackingDataAsync();

        result.MainCohort.Should().BeEmpty();
        result.UnbookedCohort.Should().BeEmpty();
    }

    [HumansFact]
    public async Task SetCampSetupAsync_rejects_offset_at_or_after_zero()
    {
        var es = MakeEvent(buildStartOffset: -5);
        var userId = Guid.NewGuid();
        var sut = BuildSut(es);

        var result = await sut.SetCampSetupAsync(
            userId, es.GateOpeningDate, notes: null, coordinatorUserId: Guid.NewGuid());

        result.Ok.Should().BeFalse();
        result.ErrorMessageKey.Should().Be("VolTrack_Err_SetupAtOrAfterGateOpen");
    }

    [HumansFact]
    public async Task SetCampSetupAsync_rejects_date_before_first_signup()
    {
        var es = MakeEvent(buildStartOffset: -10);
        var userId = Guid.NewGuid();
        var signups = new List<EligibleBuildSignup>
        {
            new(userId, -5, SignupStatus.Confirmed, "Cleanup"),
            new(userId, -4, SignupStatus.Confirmed, "Cleanup"),
        };
        var sut = BuildSut(es, signups: signups);

        // Setup date at offset -8 (before first signup at -5).
        var result = await sut.SetCampSetupAsync(
            userId, es.GateOpeningDate.PlusDays(-8), notes: null, coordinatorUserId: Guid.NewGuid());

        result.Ok.Should().BeFalse();
        result.ErrorMessageKey.Should().Be("VolTrack_Err_SetupBeforeFirstSignup");
    }

    [HumansFact]
    public async Task SetCampSetupAsync_succeeds_inside_build_window()
    {
        var es = MakeEvent(buildStartOffset: -10);
        var userId = Guid.NewGuid();
        var coordinatorId = Guid.NewGuid();
        var signups = new List<EligibleBuildSignup>
        {
            new(userId, -5, SignupStatus.Confirmed, "Cleanup"),
        };
        var trackingRepo = new FakeVolunteerTrackingRepository(signups, []);
        var sut = BuildSut(es, signups: signups, trackingRepo: trackingRepo);
        var setupDate = es.GateOpeningDate.PlusDays(-3);

        var result = await sut.SetCampSetupAsync(userId, setupDate, "left for setup", coordinatorId);

        result.Ok.Should().BeTrue();
        result.ErrorMessageKey.Should().BeNull();
        trackingRepo.UpsertCalls.Should().HaveCount(1);
        var call = trackingRepo.UpsertCalls[0];
        call.UserId.Should().Be(userId);
        call.EventSettingsId.Should().Be(es.Id);
        call.Date.Should().Be(setupDate);
        call.Notes.Should().Be("left for setup");
        call.SetByUserId.Should().Be(coordinatorId);
        call.SetAt.Should().Be(TestNow);
    }

    [HumansFact]
    public async Task ClearCampSetupAsync_nulls_camp_setup_fields()
    {
        var es = MakeEvent(buildStartOffset: -10);
        var userId = Guid.NewGuid();
        var coordinatorId = Guid.NewGuid();
        var bs = new VolunteerBuildStatus
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            EventSettingsId = es.Id,
            BarrioSetupStartDate = es.GateOpeningDate.PlusDays(-3),
            Notes = "left",
            SetByUserId = Guid.NewGuid(),
            SetAt = TestNow,
        };
        var trackingRepo = new FakeVolunteerTrackingRepository(
            [], [bs]);
        var sut = BuildSut(es, buildStatuses: [bs], trackingRepo: trackingRepo);

        await sut.ClearCampSetupAsync(userId, coordinatorId);

        trackingRepo.UpsertCalls.Should().HaveCount(1);
        var call = trackingRepo.UpsertCalls[0];
        call.Date.Should().BeNull();
        call.Notes.Should().BeNull();
        call.SetByUserId.Should().BeNull();
        call.SetAt.Should().BeNull();
        var stored = trackingRepo.BuildStatuses.Single();
        stored.BarrioSetupStartDate.Should().BeNull();
        stored.Notes.Should().BeNull();
        stored.SetByUserId.Should().BeNull();
        stored.SetAt.Should().BeNull();
    }

    // ----------------------------------------------------------------------
    // SetDayOff / ClearDayOff
    // ----------------------------------------------------------------------

    [HumansFact]
    public async Task SetDayOffAsync_rejects_offset_outside_build_window()
    {
        var es = MakeEvent(buildStartOffset: -5);
        var sut = BuildSut(es);

        var below = await sut.SetDayOffAsync(Guid.NewGuid(), -6, reason: null, Guid.NewGuid());
        var above = await sut.SetDayOffAsync(Guid.NewGuid(), 0, reason: null, Guid.NewGuid());

        below.Ok.Should().BeFalse();
        below.ErrorMessageKey.Should().Be("VolTrack_Err_DayOffOutsideBuild");
        above.Ok.Should().BeFalse();
        above.ErrorMessageKey.Should().Be("VolTrack_Err_DayOffOutsideBuild");
    }

    [HumansFact]
    public async Task SetDayOffAsync_rejects_when_user_has_confirmed_signup_that_day()
    {
        var es = MakeEvent(buildStartOffset: -5);
        var userId = Guid.NewGuid();
        var signups = new List<EligibleBuildSignup>
        {
            new(userId, -3, SignupStatus.Confirmed, "Cleanup"),
        };
        var sut = BuildSut(es, signups: signups);

        var result = await sut.SetDayOffAsync(userId, -3, reason: null, Guid.NewGuid());

        result.Ok.Should().BeFalse();
        result.ErrorMessageKey.Should().Be("VolTrack_Err_DayOffWithSignups");
    }

    [HumansFact]
    public async Task SetDayOffAsync_rejects_when_user_has_pending_signup_that_day()
    {
        var es = MakeEvent(buildStartOffset: -5);
        var userId = Guid.NewGuid();
        var signups = new List<EligibleBuildSignup>
        {
            new(userId, -3, SignupStatus.Pending, "Cleanup"),
        };
        var sut = BuildSut(es, signups: signups);

        var result = await sut.SetDayOffAsync(userId, -3, reason: null, Guid.NewGuid());

        result.Ok.Should().BeFalse();
        result.ErrorMessageKey.Should().Be("VolTrack_Err_DayOffWithSignups");
    }

    [HumansFact]
    public async Task SetDayOffAsync_succeeds_when_day_is_a_gap()
    {
        var es = MakeEvent(buildStartOffset: -5);
        var userId = Guid.NewGuid();
        var coordId = Guid.NewGuid();
        var trackingRepo = new FakeVolunteerTrackingRepository(
            [], []);
        var sut = BuildSut(es, trackingRepo: trackingRepo);

        var result = await sut.SetDayOffAsync(userId, -3, "doctor", coordId);

        result.Ok.Should().BeTrue();
        result.ErrorMessageKey.Should().BeNull();
        trackingRepo.UpsertDayOffCalls.Should().HaveCount(1);
        var entry = trackingRepo.UpsertDayOffCalls[0].Entry;
        entry.DayOffset.Should().Be(-3);
        entry.Reason.Should().Be("doctor");
        entry.MarkedByUserId.Should().Be(coordId);
        entry.MarkedAt.Should().Be(TestNow);
    }

    [HumansFact]
    public async Task SetDayOffAsync_replaces_reason_when_called_twice_on_same_day()
    {
        var es = MakeEvent(buildStartOffset: -5);
        var userId = Guid.NewGuid();
        var trackingRepo = new FakeVolunteerTrackingRepository(
            [], []);
        var sut = BuildSut(es, trackingRepo: trackingRepo);

        await sut.SetDayOffAsync(userId, -3, "doctor", Guid.NewGuid());
        await sut.SetDayOffAsync(userId, -3, "city visit", Guid.NewGuid());

        var stored = trackingRepo.BuildStatuses.Single();
        stored.DayOffs.Should().HaveCount(1);
        stored.DayOffs[0].Reason.Should().Be("city visit");
    }

    [HumansFact]
    public async Task SetDayOffAsync_trims_reason_and_truncates_at_200_chars()
    {
        var es = MakeEvent(buildStartOffset: -5);
        var trackingRepo = new FakeVolunteerTrackingRepository(
            [], []);
        var sut = BuildSut(es, trackingRepo: trackingRepo);

        await sut.SetDayOffAsync(Guid.NewGuid(), -3, "   ", Guid.NewGuid());
        var blank = trackingRepo.UpsertDayOffCalls.Last().Entry.Reason;

        var oversized = new string('x', 250);
        await sut.SetDayOffAsync(Guid.NewGuid(), -2, oversized, Guid.NewGuid());
        var capped = trackingRepo.UpsertDayOffCalls.Last().Entry.Reason;

        blank.Should().BeNull();
        capped.Should().NotBeNull();
        capped.Length.Should().Be(200);
    }

    [HumansFact]
    public async Task SetDayOffAsync_does_not_validate_camp_setup_overlap()
    {
        // Regression guard: earlier drafts of the spec validated camp-setup
        // overlap server-side. The redesign removed it — the UI prevents it
        // by hiding the action on CampSetup cells; defense-in-depth is the
        // auto-cleanup that runs when camp-setup is moved.
        var es = MakeEvent(buildStartOffset: -10);
        var userId = Guid.NewGuid();
        var bs = new VolunteerBuildStatus
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            EventSettingsId = es.Id,
            BarrioSetupStartDate = es.GateOpeningDate.PlusDays(-3),  // span = -3..-1
        };
        var trackingRepo = new FakeVolunteerTrackingRepository(
            [], [bs]);
        var sut = BuildSut(es, buildStatuses: [bs], trackingRepo: trackingRepo);

        // -2 is INSIDE the camp-setup span. The service does NOT reject.
        var result = await sut.SetDayOffAsync(userId, -2, "doctor", Guid.NewGuid());

        result.Ok.Should().BeTrue();
    }

    [HumansFact]
    public async Task ClearDayOffAsync_removes_entry_when_present()
    {
        var es = MakeEvent(buildStartOffset: -5);
        var userId = Guid.NewGuid();
        var bs = new VolunteerBuildStatus
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            EventSettingsId = es.Id,
            DayOffs =
            [
                new(-3, "doctor", Guid.NewGuid(), TestNow)
            ],
        };
        var trackingRepo = new FakeVolunteerTrackingRepository(
            [], [bs]);
        var sut = BuildSut(es, buildStatuses: [bs], trackingRepo: trackingRepo);

        var result = await sut.ClearDayOffAsync(userId, -3, Guid.NewGuid());

        result.Removed.Should().BeTrue();
        bs.DayOffs.Should().BeEmpty();
    }

    [HumansFact]
    public async Task ClearDayOffAsync_is_idempotent_when_entry_absent()
    {
        var es = MakeEvent(buildStartOffset: -5);
        var trackingRepo = new FakeVolunteerTrackingRepository(
            [], []);
        var sut = BuildSut(es, trackingRepo: trackingRepo);

        var result = await sut.ClearDayOffAsync(Guid.NewGuid(), -3, Guid.NewGuid());

        result.Removed.Should().BeFalse();
    }

    [HumansFact]
    public async Task MainCohort_dayoff_renders_DayOff_state_and_does_not_count_as_gap()
    {
        var es = MakeEvent(buildStartOffset: -5);
        var userId = Guid.NewGuid();
        var signups = new List<EligibleBuildSignup>
        {
            new(userId, -5, SignupStatus.Confirmed, "Cleanup"),
            new(userId, -1, SignupStatus.Confirmed, "Cleanup"),
        };
        var participations = new[] { Participation(userId, ParticipationStatus.Ticketed, es.Year) };
        var bs = new VolunteerBuildStatus
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            EventSettingsId = es.Id,
            DayOffs =
            [
                new(-3, "doctor", Guid.NewGuid(), TestNow)
            ],
        };
        var sut = BuildSut(es, signups: signups, participations: participations, buildStatuses: [bs]);

        var result = await sut.GetTrackingDataAsync();

        var row = result.MainCohort.Single();
        // -5 Confirmed, -4 Gap, -3 DayOff (suppresses gap), -2 Gap, -1 Confirmed.
        row.GapCount.Should().Be(2);
        row.Cells.Single(c => c.DayOffset == -3).State.Should().Be(VolunteerCellState.DayOff);
        row.DayOffs.Should().HaveCount(1);
        row.DayOffs[0].Reason.Should().Be("doctor");
    }

    [HumansFact]
    public async Task SetCampSetupAsync_auto_clears_dayoffs_now_inside_span()
    {
        var es = MakeEvent(buildStartOffset: -10);
        var userId = Guid.NewGuid();
        var coordId = Guid.NewGuid();
        var signups = new List<EligibleBuildSignup>
        {
            new(userId, -8, SignupStatus.Confirmed, "Cleanup"),
        };
        var bs = new VolunteerBuildStatus
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            EventSettingsId = es.Id,
            DayOffs =
            [
                new(-9, "early", Guid.NewGuid(), TestNow), // before new span
                new(-6, "soon", Guid.NewGuid(), TestNow) // inside new span
            ],
        };
        var trackingRepo = new FakeVolunteerTrackingRepository(signups, [bs]);
        var sut = BuildSut(es, signups: signups, buildStatuses: [bs], trackingRepo: trackingRepo);

        // New camp-setup at -7 → span covers -7, -6, -5, ..., -1.
        var result = await sut.SetCampSetupAsync(
            userId, es.GateOpeningDate.PlusDays(-7), notes: null, coordId);

        result.Ok.Should().BeTrue();
        result.AutoClearedDayOffs.Should().Equal(-6);
        bs.DayOffs.Select(d => d.DayOffset).Should().Equal(-9);
    }

    [HumansFact]
    public async Task SetCampSetupAsync_returns_empty_AutoClearedDayOffs_when_no_overlap()
    {
        var es = MakeEvent(buildStartOffset: -10);
        var userId = Guid.NewGuid();
        var bs = new VolunteerBuildStatus
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            EventSettingsId = es.Id,
            DayOffs =
            [
                new(-9, "early", Guid.NewGuid(), TestNow)
            ],
        };
        var trackingRepo = new FakeVolunteerTrackingRepository(
            [], [bs]);
        var sut = BuildSut(es, buildStatuses: [bs], trackingRepo: trackingRepo);

        var result = await sut.SetCampSetupAsync(
            userId, es.GateOpeningDate.PlusDays(-3), notes: null, Guid.NewGuid());

        result.Ok.Should().BeTrue();
        result.AutoClearedDayOffs.Should().BeEmpty();
        bs.DayOffs.Should().HaveCount(1);
    }

    private static GeneralAvailability Availability(Guid userId, Guid eventSettingsId, IReadOnlyList<int> days)
        => new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            EventSettingsId = eventSettingsId,
            AvailableDayOffsets = days.ToList(),
            CreatedAt = TestNow,
            UpdatedAt = TestNow,
        };

    private static EventParticipation Participation(Guid userId, ParticipationStatus status, int year)
        => new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Year = year,
            Status = status,
            Source = ParticipationSource.UserDeclared,
        };

    // ----------------------------------------------------------------------
    // Test SUT builder with fakes
    // ----------------------------------------------------------------------

    private static VolunteerTrackingService BuildSut(
        EventSettings? activeEvent,
        IReadOnlyList<EligibleBuildSignup>? signups = null,
        IReadOnlyList<VolunteerBuildStatus>? buildStatuses = null,
        IReadOnlyList<EventParticipation>? participations = null,
        IReadOnlyList<GeneralAvailability>? availabilities = null,
        Instant? now = null,
        FakeVolunteerTrackingRepository? trackingRepo = null)
    {
        var clock = new FakeClock(now ?? TestNow);

        var shiftMgmt = Substitute.For<IShiftManagementRepository>();
        shiftMgmt.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(activeEvent);

        var availabilityRepo = Substitute.For<IGeneralAvailabilityRepository>();
        availabilityRepo
            .GetByEventAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var eventId = call.Arg<Guid>();
                var rows = (availabilities ?? [])
                    .Where(a => a.EventSettingsId == eventId).ToList();
                return Task.FromResult<IReadOnlyList<GeneralAvailability>>(rows);
            });

        var userService = Substitute.For<IUserService>();
        userService.GetAllParticipationsForYearAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var year = call.Arg<int>();
                return Task.FromResult(
                    (participations ?? [])
                    .Where(p => p.Year == year).ToList());
            });

        trackingRepo ??= new FakeVolunteerTrackingRepository(
            signups ?? [],
            buildStatuses ?? []);

        return new VolunteerTrackingService(
            trackingRepo, shiftMgmt, availabilityRepo, userService, Substitute.For<IShiftViewInvalidator>(), clock);
    }

    private static EventSettings MakeEvent(int buildStartOffset = -5, LocalDate? gateOpening = null)
        => new()
        {
            Id = Guid.NewGuid(),
            EventName = "Test Event",
            Year = 2026,
            TimeZoneId = "Europe/Madrid",
            GateOpeningDate = gateOpening ?? DefaultGateOpening,
            BuildStartOffset = buildStartOffset,
            EventEndOffset = 6,
            StrikeEndOffset = 9,
            IsActive = true,
            CreatedAt = TestNow,
            UpdatedAt = TestNow,
        };

    // ----------------------------------------------------------------------
    // Fake repository that captures mutations
    // ----------------------------------------------------------------------

    private sealed class FakeVolunteerTrackingRepository(
        IReadOnlyList<EligibleBuildSignup> signups,
        IReadOnlyList<VolunteerBuildStatus> buildStatuses) : IVolunteerTrackingRepository
    {
        public List<VolunteerBuildStatus> BuildStatuses { get; } = buildStatuses.ToList();

        public List<(Guid UserId, Guid EventSettingsId, LocalDate? Date, string? Notes, Guid? SetByUserId, Instant? SetAt)> UpsertCalls { get; } =
            [];
        public List<(Guid UserId, Guid EventSettingsId, DayOffEntry Entry)> UpsertDayOffCalls { get; } = [];
        public List<(Guid UserId, Guid EventSettingsId, int DayOffset)> RemoveDayOffCalls { get; } = [];

        public Task<VolunteerBuildStatus?> GetAsync(Guid userId, Guid eventSettingsId, CancellationToken ct = default)
            => Task.FromResult(BuildStatuses.FirstOrDefault(b => b.UserId == userId && b.EventSettingsId == eventSettingsId));

        public Task<IReadOnlyList<VolunteerBuildStatus>> GetByEventAsync(Guid eventSettingsId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<VolunteerBuildStatus>>(
                BuildStatuses.Where(b => b.EventSettingsId == eventSettingsId).ToList());

        public Task<IReadOnlyList<VolunteerBuildStatus>> GetByUsersAndEventAsync(
            IReadOnlyCollection<Guid> userIds, Guid eventSettingsId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<VolunteerBuildStatus>>(
                BuildStatuses
                    .Where(b => b.EventSettingsId == eventSettingsId && userIds.Contains(b.UserId))
                    .ToList());

        public Task<IReadOnlyList<int>> UpsertCampSetupAsync(
            Guid userId, Guid eventSettingsId, LocalDate? barrioSetupStartDate,
            string? notes, Guid? setByUserId, Instant? setAt,
            int? setupOffsetThreshold, CancellationToken ct = default)
        {
            UpsertCalls.Add((userId, eventSettingsId, barrioSetupStartDate, notes, setByUserId, setAt));
            var existing = BuildStatuses.FirstOrDefault(b => b.UserId == userId && b.EventSettingsId == eventSettingsId);
            if (existing is null)
            {
                existing = new VolunteerBuildStatus
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    EventSettingsId = eventSettingsId,
                };
                BuildStatuses.Add(existing);
            }
            existing.BarrioSetupStartDate = barrioSetupStartDate;
            existing.Notes = notes;
            existing.SetByUserId = setByUserId;
            existing.SetAt = setAt;

            IReadOnlyList<int> trimmed = [];
            if (setupOffsetThreshold is { } threshold)
            {
                var toTrim = existing.DayOffs
                    .Where(d => d.DayOffset >= threshold)
                    .Select(d => d.DayOffset)
                    .ToArray();
                if (toTrim.Length > 0)
                {
                    existing.DayOffs.RemoveAll(d => d.DayOffset >= threshold);
                    trimmed = toTrim;
                }
            }
            return Task.FromResult(trimmed);
        }

        public Task UpsertDayOffAsync(
            Guid userId, Guid eventSettingsId, DayOffEntry entry, CancellationToken ct = default)
        {
            UpsertDayOffCalls.Add((userId, eventSettingsId, entry));
            var existing = BuildStatuses.FirstOrDefault(b => b.UserId == userId && b.EventSettingsId == eventSettingsId);
            if (existing is null)
            {
                existing = new VolunteerBuildStatus
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    EventSettingsId = eventSettingsId,
                };
                BuildStatuses.Add(existing);
            }
            existing.DayOffs.RemoveAll(d => d.DayOffset == entry.DayOffset);
            existing.DayOffs.Add(entry);
            existing.DayOffs.Sort((a, b) => a.DayOffset.CompareTo(b.DayOffset));
            return Task.CompletedTask;
        }

        public Task<bool> RemoveDayOffAsync(
            Guid userId, Guid eventSettingsId, int dayOffset, CancellationToken ct = default)
        {
            RemoveDayOffCalls.Add((userId, eventSettingsId, dayOffset));
            var existing = BuildStatuses.FirstOrDefault(b => b.UserId == userId && b.EventSettingsId == eventSettingsId);
            if (existing is null) return Task.FromResult(false);
            var removed = existing.DayOffs.RemoveAll(d => d.DayOffset == dayOffset) > 0;
            return Task.FromResult(removed);
        }

        public Task<IReadOnlyList<EligibleBuildSignup>> GetEligibleBuildSignupsAsync(
            Guid eventSettingsId, CancellationToken ct = default)
            => Task.FromResult(signups);

        public Task<IReadOnlyList<ConfirmedShiftRow>> GetConfirmedShiftsInRangeAsync(
            Guid eventSettingsId,
            LocalDate startDate,
            LocalDate endDate,
            Guid? departmentId,
            CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ConfirmedShiftRow>>([]);
    }
}
