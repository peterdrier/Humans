using AwesomeAssertions;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories.Shifts;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Testing;
using Xunit;

namespace Humans.Application.Tests.Repositories;

/// <summary>
/// Unit tests for <see cref="ShiftSignupRepository"/> — focused on the
/// behaviors the service relies on (duplicate detection, capacity counting,
/// active-signup filtering, block load-for-mutation). Full state-machine
/// coverage lives in <c>ShiftSignupServiceTests</c>.
/// </summary>
public class ShiftSignupRepositoryTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly ShiftSignupRepository _repo;

    private static readonly Instant TestNow = Instant.FromUtc(2026, 6, 15, 12, 0);

    public ShiftSignupRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new HumansDbContext(options);
        _repo = new ShiftSignupRepository(_dbContext, new FakeClock(TestNow));
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [HumansFact]
    public async Task HasActiveSignupAsync_ReturnsTrueForPendingOrConfirmed()
    {
        var userId = Guid.NewGuid();
        var shiftId = Guid.NewGuid();

        _dbContext.ShiftSignups.Add(MakeSignup(userId, shiftId, SignupStatus.Pending));
        await _dbContext.SaveChangesAsync();

        (await _repo.HasActiveSignupAsync(userId, shiftId)).Should().BeTrue();
    }

    [HumansFact]
    public async Task HasActiveSignupAsync_ReturnsFalseForBailedOrRefused()
    {
        var userId = Guid.NewGuid();
        var shiftId = Guid.NewGuid();

        _dbContext.ShiftSignups.Add(MakeSignup(userId, shiftId, SignupStatus.Bailed));
        _dbContext.ShiftSignups.Add(MakeSignup(userId, shiftId, SignupStatus.Refused));
        await _dbContext.SaveChangesAsync();

        (await _repo.HasActiveSignupAsync(userId, shiftId)).Should().BeFalse();
    }

    [HumansFact]
    public async Task GetActiveShiftIdsForUserAsync_FiltersToPendingAndConfirmed()
    {
        var userId = Guid.NewGuid();
        var active = Guid.NewGuid();
        var bailed = Guid.NewGuid();
        var otherUserShift = Guid.NewGuid();

        _dbContext.ShiftSignups.Add(MakeSignup(userId, active, SignupStatus.Confirmed));
        _dbContext.ShiftSignups.Add(MakeSignup(userId, bailed, SignupStatus.Bailed));
        _dbContext.ShiftSignups.Add(MakeSignup(Guid.NewGuid(), otherUserShift, SignupStatus.Confirmed));
        await _dbContext.SaveChangesAsync();

        var result = await _repo.GetActiveShiftIdsForUserAsync(
            userId, new[] { active, bailed, otherUserShift });

        result.Should().BeEquivalentTo(new[] { active });
    }

    [HumansFact]
    public async Task GetConfirmedCountsByShiftAsync_ExcludesNonConfirmedAndMissingShifts()
    {
        var shiftA = Guid.NewGuid();
        var shiftB = Guid.NewGuid();

        _dbContext.ShiftSignups.Add(MakeSignup(Guid.NewGuid(), shiftA, SignupStatus.Confirmed));
        _dbContext.ShiftSignups.Add(MakeSignup(Guid.NewGuid(), shiftA, SignupStatus.Confirmed));
        _dbContext.ShiftSignups.Add(MakeSignup(Guid.NewGuid(), shiftA, SignupStatus.Pending));
        _dbContext.ShiftSignups.Add(MakeSignup(Guid.NewGuid(), shiftB, SignupStatus.Cancelled));
        await _dbContext.SaveChangesAsync();

        var counts = await _repo.GetConfirmedCountsByShiftAsync(new[] { shiftA, shiftB });

        counts[shiftA].Should().Be(2);
        counts.ContainsKey(shiftB).Should().BeFalse();
    }

    [HumansFact]
    public async Task GetConfirmedCountsByShiftAsync_EmptyInputReturnsEmpty()
    {
        var counts = await _repo.GetConfirmedCountsByShiftAsync(Array.Empty<Guid>());
        counts.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Add_AndSaveChangesAsync_Persists()
    {
        var userId = Guid.NewGuid();
        var shiftId = Guid.NewGuid();

        _repo.Add(MakeSignup(userId, shiftId, SignupStatus.Pending));
        await _repo.SaveChangesAsync();

        (await _dbContext.ShiftSignups.AsNoTracking().CountAsync(s => s.UserId == userId))
            .Should().Be(1);
    }

    [HumansFact]
    public async Task GetDistinctEeUsersOnDayAsync_CountsDistinctConfirmedUsers()
    {
        var esId = Guid.NewGuid();
        var otherEs = Guid.NewGuid();

        // EE shifts use negative DayOffsets (see Shift.IsEarlyEntry).
        var shiftDayMinus3A = SeedShiftWithRota(esId, dayOffset: -3);
        var shiftDayMinus3B = SeedShiftWithRota(esId, dayOffset: -3);
        var shiftDayMinus4 = SeedShiftWithRota(esId, dayOffset: -4);
        var shiftOtherEs = SeedShiftWithRota(otherEs, dayOffset: -3);

        var user1 = Guid.NewGuid();
        var user2 = Guid.NewGuid();

        // Same user, two EE shifts on day -3 → counts once.
        _dbContext.ShiftSignups.Add(MakeSignup(user1, shiftDayMinus3A, SignupStatus.Confirmed));
        _dbContext.ShiftSignups.Add(MakeSignup(user1, shiftDayMinus3B, SignupStatus.Confirmed));
        _dbContext.ShiftSignups.Add(MakeSignup(user2, shiftDayMinus3A, SignupStatus.Confirmed));
        // Different day or different event → excluded.
        _dbContext.ShiftSignups.Add(MakeSignup(user1, shiftDayMinus4, SignupStatus.Confirmed));
        _dbContext.ShiftSignups.Add(MakeSignup(user2, shiftOtherEs, SignupStatus.Confirmed));
        // Non-confirmed → excluded.
        _dbContext.ShiftSignups.Add(MakeSignup(Guid.NewGuid(), shiftDayMinus3A, SignupStatus.Pending));
        await _dbContext.SaveChangesAsync();

        var count = await _repo.GetDistinctEeUsersOnDayAsync(esId, -3);
        count.Should().Be(2);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static ShiftSignup MakeSignup(Guid userId, Guid shiftId, SignupStatus status) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        ShiftId = shiftId,
        Status = status,
        CreatedAt = TestNow,
        UpdatedAt = TestNow
    };

    private Guid SeedShiftWithRota(Guid esId, int dayOffset)
    {
        var teamId = _dbContext.Rotas.AsNoTracking()
            .Where(r => r.EventSettingsId == esId)
            .Select(r => r.TeamId)
            .FirstOrDefault();

        if (teamId == Guid.Empty)
        {
            teamId = Guid.NewGuid();
            _dbContext.Teams.Add(new Team
            {
                Id = teamId,
                Name = "TestTeam-" + teamId.ToString()[..8],
                Slug = "team-" + teamId.ToString()[..8],
                IsActive = true
            });
        }

        // Reuse/create EventSettings for this esId.
        if (!_dbContext.EventSettings.Any(e => e.Id == esId))
        {
            _dbContext.EventSettings.Add(new EventSettings
            {
                Id = esId,
                EventName = "TestEvent",
                GateOpeningDate = new LocalDate(2026, 7, 1),
                TimeZoneId = "UTC"
            });
        }

        var rotaId = Guid.NewGuid();
        _dbContext.Rotas.Add(new Rota
        {
            Id = rotaId,
            Name = "TestRota",
            TeamId = teamId,
            EventSettingsId = esId,
            Policy = SignupPolicy.Public,
            Period = RotaPeriod.Event
        });

        var shiftId = Guid.NewGuid();
        _dbContext.Shifts.Add(new Shift
        {
            Id = shiftId,
            RotaId = rotaId,
            DayOffset = dayOffset,
            IsAllDay = true,
            StartTime = new LocalTime(8, 0),
            Duration = Duration.FromHours(12),
            MaxVolunteers = 10,
        });

        _dbContext.SaveChanges();
        return shiftId;
    }
}
