using Humans.Shifts.Domain;
using AwesomeAssertions;
using Humans.Application.Interfaces.Repositories;
using Humans.Shifts.Tests.Infrastructure;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Shifts.Data;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Testing;

using Humans.Shifts.Contracts;
namespace Humans.Shifts.Tests.Repositories;

/// <summary>
/// Unit tests for <see cref="ShiftRepository"/> — focused on the
/// behaviors the service relies on (duplicate detection, capacity counting,
/// active-signup filtering, block load-for-mutation). Full state-machine
/// coverage lives in <c>ShiftSignupServiceTests</c>.
/// </summary>
public class ShiftRepositorySignupTests : IDisposable
{
    private readonly ShiftsDbContext _shiftsDbContext;
    private readonly ShiftRepository _repo;

    private static readonly Instant TestNow = Instant.FromUtc(2026, 6, 15, 12, 0);

    public ShiftRepositorySignupTests()
    {
        var shiftsOptions = new DbContextOptionsBuilder<ShiftsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _shiftsDbContext = new ShiftsDbContext(shiftsOptions);
        _repo = new ShiftRepository(new TestDbContextFactory<ShiftsDbContext>(shiftsOptions), _shiftsDbContext, new FakeClock(TestNow));
    }

    public void Dispose()
    {
        _shiftsDbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [HumansFact]
    public async Task GetActiveShiftIdsForUserAsync_FiltersToPendingAndConfirmed()
    {
        var userId = Guid.NewGuid();
        var active = Guid.NewGuid();
        var bailed = Guid.NewGuid();
        var otherUserShift = Guid.NewGuid();

        _shiftsDbContext.ShiftSignups.Add(MakeSignup(userId, active, SignupStatus.Confirmed));
        _shiftsDbContext.ShiftSignups.Add(MakeSignup(userId, bailed, SignupStatus.Bailed));
        _shiftsDbContext.ShiftSignups.Add(MakeSignup(Guid.NewGuid(), otherUserShift, SignupStatus.Confirmed));
        await _shiftsDbContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var result = await _repo.GetActiveShiftIdsForUserAsync(
            userId, [active, bailed, otherUserShift], Xunit.TestContext.Current.CancellationToken);

        result.Should().BeEquivalentTo([active]);
    }

    [HumansFact(Timeout = 10000)]
    public async Task GetConfirmedSignupCountsByShiftAsync_ExcludesNonConfirmedAndMissingShifts()
    {
        var shiftA = Guid.NewGuid();
        var shiftB = Guid.NewGuid();

        _shiftsDbContext.ShiftSignups.Add(MakeSignup(Guid.NewGuid(), shiftA, SignupStatus.Confirmed));
        _shiftsDbContext.ShiftSignups.Add(MakeSignup(Guid.NewGuid(), shiftA, SignupStatus.Confirmed));
        _shiftsDbContext.ShiftSignups.Add(MakeSignup(Guid.NewGuid(), shiftA, SignupStatus.Pending));
        _shiftsDbContext.ShiftSignups.Add(MakeSignup(Guid.NewGuid(), shiftB, SignupStatus.Cancelled));
        await _shiftsDbContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var counts = await _repo.GetConfirmedSignupCountsByShiftAsync([shiftA, shiftB], Xunit.TestContext.Current.CancellationToken);

        counts[shiftA].Should().Be(2);
        counts.ContainsKey(shiftB).Should().BeFalse();
    }

    [HumansFact]
    public async Task GetConfirmedSignupCountsByShiftAsync_EmptyInputReturnsEmpty()
    {
        var counts = await _repo.GetConfirmedSignupCountsByShiftAsync([], Xunit.TestContext.Current.CancellationToken);
        counts.Should().BeEmpty();
    }

    [HumansFact]
    public async Task AddRange_AndSaveChangesAsync_Persists()
    {
        var userId = Guid.NewGuid();
        var shiftId = Guid.NewGuid();

        _repo.AddRange([MakeSignup(userId, shiftId, SignupStatus.Pending)]);
        await _repo.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        (await _shiftsDbContext.ShiftSignups.AsNoTracking().CountAsync(s => s.UserId == userId, Xunit.TestContext.Current.CancellationToken))
            .Should().Be(1);
    }

    [HumansFact]
    public async Task GetUserIdsForDayAsync_FiltersEventDayAndStatusScope()
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
        var pendingUser = Guid.NewGuid();

        // Same user, two EE shifts on day -3 → counts once.
        _shiftsDbContext.ShiftSignups.Add(MakeSignup(user1, shiftDayMinus3A, SignupStatus.Confirmed));
        _shiftsDbContext.ShiftSignups.Add(MakeSignup(user1, shiftDayMinus3B, SignupStatus.Confirmed));
        _shiftsDbContext.ShiftSignups.Add(MakeSignup(user2, shiftDayMinus3A, SignupStatus.Confirmed));
        // Different day or different event → excluded.
        _shiftsDbContext.ShiftSignups.Add(MakeSignup(user1, shiftDayMinus4, SignupStatus.Confirmed));
        _shiftsDbContext.ShiftSignups.Add(MakeSignup(user2, shiftOtherEs, SignupStatus.Confirmed));
        // Non-confirmed → excluded.
        _shiftsDbContext.ShiftSignups.Add(MakeSignup(pendingUser, shiftDayMinus3A, SignupStatus.Pending));
        await _shiftsDbContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var confirmed = await _repo.GetUserIdsForDayAsync(
            esId, -3, ShiftDayUserStatusScope.ConfirmedOnly, Xunit.TestContext.Current.CancellationToken);
        confirmed.Should().BeEquivalentTo([user1, user2]);

        var pendingOrConfirmed = await _repo.GetUserIdsForDayAsync(
            esId, -3, ShiftDayUserStatusScope.PendingOrConfirmed, Xunit.TestContext.Current.CancellationToken);
        pendingOrConfirmed.Should().BeEquivalentTo([user1, user2, pendingUser]);
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
        var teamId = _shiftsDbContext.Rotas.AsNoTracking()
            .Where(r => r.EventSettingsId == esId)
            .Select(r => r.TeamId)
            .FirstOrDefault();

        if (teamId == Guid.Empty)
        {
            teamId = Guid.NewGuid();
        }

        // Reuse/create EventSettings for this esId.
        if (!_shiftsDbContext.EventSettings.Any(e => e.Id == esId))
        {
            _shiftsDbContext.EventSettings.Add(new EventSettings
            {
                Id = esId,
                EventName = "TestEvent",
                GateOpeningDate = new LocalDate(2026, 7, 1),
                TimeZoneId = "UTC"
            });
        }

        var rotaId = Guid.NewGuid();
        _shiftsDbContext.Rotas.Add(new Rota
        {
            Id = rotaId,
            Name = "TestRota",
            TeamId = teamId,
            EventSettingsId = esId,
            Policy = SignupPolicy.Public,
            Period = RotaPeriod.Event
        });

        var shiftId = Guid.NewGuid();
        _shiftsDbContext.Shifts.Add(new Shift
        {
            Id = shiftId,
            RotaId = rotaId,
            DayOffset = dayOffset,
            IsAllDay = true,
            StartTime = new LocalTime(8, 0),
            Duration = Duration.FromHours(12),
            MaxVolunteers = 10,
        });

        _shiftsDbContext.SaveChanges();
        return shiftId;
    }
}
