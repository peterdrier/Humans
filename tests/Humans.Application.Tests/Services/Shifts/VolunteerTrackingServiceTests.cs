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
