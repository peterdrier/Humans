using AwesomeAssertions;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Shifts;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Testing;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

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
        row.LastEligibleSignupOffset.Should().Be(-2);
        row.Cells.Should().HaveCount(5);
        // Cells -5..-2 are Confirmed; cell -1 is Expected (today inside active window).
        row.Cells.Single(c => c.DayOffset == -5).State.Should().Be(VolunteerCellState.Confirmed);
        row.Cells.Single(c => c.DayOffset == -4).State.Should().Be(VolunteerCellState.Confirmed);
        row.Cells.Single(c => c.DayOffset == -3).State.Should().Be(VolunteerCellState.Confirmed);
        row.Cells.Single(c => c.DayOffset == -2).State.Should().Be(VolunteerCellState.Confirmed);
        row.Cells.Single(c => c.DayOffset == -1).State.Should().BeOneOf(VolunteerCellState.Expected, VolunteerCellState.Outside);
    }

    [HumansFact]
    public async Task MainCohort_mid_window_gap_renders_red_cell()
    {
        var es = MakeEvent(buildStartOffset: -5);
        var userId = Guid.NewGuid();
        // Signups at -5, -4, -2: missing -3. Today is offset -1.
        var signups = new List<EligibleBuildSignup>
        {
            new(userId, -5, SignupStatus.Confirmed, "Cleanup"),
            new(userId, -4, SignupStatus.Confirmed, "Cleanup"),
            new(userId, -2, SignupStatus.Confirmed, "Cleanup"),
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

        var sut = BuildSut(es, signups: signups, participations: participations, buildStatuses: new[] { bs });

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
    public async Task MainCohort_block_on_empty_day_suppresses_gap()
    {
        var es = MakeEvent(buildStartOffset: -5);
        var userId = Guid.NewGuid();
        var signups = new List<EligibleBuildSignup>
        {
            new(userId, -5, SignupStatus.Confirmed, "Cleanup"),
            new(userId, -4, SignupStatus.Confirmed, "Cleanup"),
            new(userId, -2, SignupStatus.Confirmed, "Cleanup"),
        };
        var participations = new[] { Participation(userId, ParticipationStatus.Ticketed, es.Year) };
        var bs = new VolunteerBuildStatus
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            EventSettingsId = es.Id,
            BlockedDayOffsets = new List<int> { -3 },
        };

        var sut = BuildSut(es, signups: signups, participations: participations, buildStatuses: new[] { bs });

        var result = await sut.GetTrackingDataAsync();

        var row = result.MainCohort.Single();
        row.GapCount.Should().Be(0);
        row.Cells.Single(c => c.DayOffset == -3).State.Should().Be(VolunteerCellState.Blocked);
    }

    [HumansFact]
    public async Task MainCohort_camp_setup_wins_over_block_on_overlap()
    {
        var es = MakeEvent(buildStartOffset: -5);
        var userId = Guid.NewGuid();
        var signups = new List<EligibleBuildSignup>
        {
            new(userId, -5, SignupStatus.Confirmed, "Cleanup"),
        };
        var participations = new[] { Participation(userId, ParticipationStatus.Ticketed, es.Year) };
        var bs = new VolunteerBuildStatus
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            EventSettingsId = es.Id,
            BarrioSetupStartDate = es.GateOpeningDate.PlusDays(-3),
            BlockedDayOffsets = new List<int> { -3 },
        };

        var sut = BuildSut(es, signups: signups, participations: participations, buildStatuses: new[] { bs });

        var result = await sut.GetTrackingDataAsync();

        var row = result.MainCohort.Single();
        row.Cells.Single(c => c.DayOffset == -3).State.Should().Be(VolunteerCellState.CampSetup);
    }

    [HumansFact]
    public async Task UnbookedCohort_volunteer_with_availability_no_signups_appears()
    {
        var es = MakeEvent(buildStartOffset: -5);
        var userId = Guid.NewGuid();
        var participations = new[] { Participation(userId, ParticipationStatus.Ticketed, es.Year) };
        var availability = new[] { Availability(userId, es.Id, new[] { -5, -4, -3 }) };

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
        var availability = new[] { Availability(userId, es.Id, new[] { -5, -4, -3 }) };
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
        var availability = new[] { Availability(userId, es.Id, new[] { -5, -4, -3 }) };

        var sut = BuildSut(es, participations: participations, availabilities: availability);

        var result = await sut.GetTrackingDataAsync();

        result.MainCohort.Should().BeEmpty();
        result.UnbookedCohort.Should().BeEmpty();
    }

    [HumansFact]
    public async Task UnbookedCohort_blocked_day_renders_blocked_not_available()
    {
        var es = MakeEvent(buildStartOffset: -5);
        var userId = Guid.NewGuid();
        var participations = new[] { Participation(userId, ParticipationStatus.Ticketed, es.Year) };
        var availability = new[] { Availability(userId, es.Id, new[] { -5, -4 }) };
        var bs = new VolunteerBuildStatus
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            EventSettingsId = es.Id,
            BlockedDayOffsets = new List<int> { -5 },
        };

        var sut = BuildSut(es, participations: participations, availabilities: availability, buildStatuses: new[] { bs });

        var result = await sut.GetTrackingDataAsync();

        var row = result.UnbookedCohort.Single();
        row.Cells.Single(c => c.DayOffset == -5).State.Should().Be(VolunteerCellState.Blocked);
        row.Cells.Single(c => c.DayOffset == -4).State.Should().Be(VolunteerCellState.AvailableUnbooked);
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
        var trackingRepo = new FakeVolunteerTrackingRepository(signups, Array.Empty<VolunteerBuildStatus>());
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
                var rows = (availabilities ?? Array.Empty<GeneralAvailability>())
                    .Where(a => a.EventSettingsId == eventId).ToList();
                return Task.FromResult<IReadOnlyList<GeneralAvailability>>(rows);
            });

        var userService = Substitute.For<IUserService>();
        userService.GetAllParticipationsForYearAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var year = call.Arg<int>();
                return Task.FromResult(
                    (participations ?? Array.Empty<EventParticipation>())
                    .Where(p => p.Year == year).ToList());
            });

        trackingRepo ??= new FakeVolunteerTrackingRepository(
            signups ?? Array.Empty<EligibleBuildSignup>(),
            buildStatuses ?? Array.Empty<VolunteerBuildStatus>());

        return new VolunteerTrackingService(
            trackingRepo, shiftMgmt, availabilityRepo, userService, clock);
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

    private sealed class FakeVolunteerTrackingRepository : IVolunteerTrackingRepository
    {
        private readonly IReadOnlyList<EligibleBuildSignup> _signups;
        public List<VolunteerBuildStatus> BuildStatuses { get; }

        public List<(Guid UserId, Guid EventSettingsId, LocalDate? Date, string? Notes, Guid? SetByUserId, Instant? SetAt)> UpsertCalls { get; } = new();
        public List<(Guid UserId, Guid EventSettingsId, IReadOnlyList<int> Offsets)> ReplaceCalls { get; } = new();
        public List<(Guid UserId, Guid EventSettingsId, int DayOffset, bool Block)> SetBlockCalls { get; } = new();

        public FakeVolunteerTrackingRepository(
            IReadOnlyList<EligibleBuildSignup> signups,
            IReadOnlyList<VolunteerBuildStatus> buildStatuses)
        {
            _signups = signups;
            BuildStatuses = buildStatuses.ToList();
        }

        public Task<VolunteerBuildStatus?> GetAsync(Guid userId, Guid eventSettingsId, CancellationToken ct = default)
            => Task.FromResult(BuildStatuses.FirstOrDefault(b => b.UserId == userId && b.EventSettingsId == eventSettingsId));

        public Task<IReadOnlyList<VolunteerBuildStatus>> GetByEventAsync(Guid eventSettingsId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<VolunteerBuildStatus>>(
                BuildStatuses.Where(b => b.EventSettingsId == eventSettingsId).ToList());

        public Task<VolunteerBuildStatus> UpsertCampSetupAsync(
            Guid userId, Guid eventSettingsId, LocalDate? barrioSetupStartDate,
            string? notes, Guid? setByUserId, Instant? setAt, CancellationToken ct = default)
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
            return Task.FromResult(existing);
        }

        public Task<IReadOnlyList<int>> ReplaceBlockedDaysAsync(
            Guid userId, Guid eventSettingsId, IReadOnlyList<int> dayOffsets, CancellationToken ct = default)
        {
            ReplaceCalls.Add((userId, eventSettingsId, dayOffsets));
            var existing = BuildStatuses.FirstOrDefault(b => b.UserId == userId && b.EventSettingsId == eventSettingsId);
            IReadOnlyList<int> prior;
            if (existing is null)
            {
                prior = Array.Empty<int>();
                existing = new VolunteerBuildStatus
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    EventSettingsId = eventSettingsId,
                };
                BuildStatuses.Add(existing);
            }
            else
            {
                prior = existing.BlockedDayOffsets.ToList();
            }
            existing.BlockedDayOffsets = dayOffsets.ToList();
            return Task.FromResult(prior);
        }

        public Task<bool> SetBlockAsync(
            Guid userId, Guid eventSettingsId, int dayOffset, bool block, CancellationToken ct = default)
        {
            SetBlockCalls.Add((userId, eventSettingsId, dayOffset, block));
            var existing = BuildStatuses.FirstOrDefault(b => b.UserId == userId && b.EventSettingsId == eventSettingsId);
            if (existing is null)
            {
                if (!block) return Task.FromResult(false);
                existing = new VolunteerBuildStatus
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    EventSettingsId = eventSettingsId,
                };
                BuildStatuses.Add(existing);
            }
            var has = existing.BlockedDayOffsets.Contains(dayOffset);
            if (block && !has)
            {
                existing.BlockedDayOffsets.Add(dayOffset);
                existing.BlockedDayOffsets.Sort();
                return Task.FromResult(true);
            }
            if (!block && has)
            {
                existing.BlockedDayOffsets.Remove(dayOffset);
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }

        public Task<IReadOnlyList<EligibleBuildSignup>> GetEligibleBuildSignupsAsync(
            Guid eventSettingsId, CancellationToken ct = default)
            => Task.FromResult(_signups);
    }
}
