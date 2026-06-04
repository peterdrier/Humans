using AwesomeAssertions;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories.Shifts;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Testing;

namespace Humans.Application.Tests.Repositories;

/// <summary>
/// Smoke tests for <see cref="ShiftRepository"/>, the EF-backed
/// repository introduced in issue #541a. Covers the repository primitives
/// most likely to regress: the narrow-field pending-signup count, the
/// active-event lookup, and the tag reconcile.
/// </summary>
public sealed class ShiftRepositoryManagementTests : IDisposable
{
    private static readonly Instant TestNow = Instant.FromUtc(2026, 4, 1, 12, 0);

    private readonly HumansDbContext _dbContext;
    private readonly ShiftRepository _repo;
    private readonly FakeClock _clock = new(TestNow);

    public ShiftRepositoryManagementTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new HumansDbContext(options);
        _repo = new ShiftRepository(new TestDbContextFactory(options), _dbContext, _clock);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
    }

    [HumansFact]
    public async Task GetActiveEventSettingsAsync_ReturnsActive_IgnoresInactive()
    {
        var active = NewEvent(isActive: true);
        var inactive = NewEvent(isActive: false);
        await _dbContext.EventSettings.AddRangeAsync(active, inactive);
        await _dbContext.SaveChangesAsync();

        var result = await _repo.GetActiveEventSettingsAsync();

        result.Should().NotBeNull();
        result.Id.Should().Be(active.Id);
    }

    [HumansFact]
    public async Task AnyOtherActiveEventSettingsAsync_ExcludesGivenId()
    {
        var es = NewEvent(isActive: true);
        _dbContext.EventSettings.Add(es);
        await _dbContext.SaveChangesAsync();

        (await _repo.AnyOtherActiveEventSettingsAsync(excludingId: es.Id)).Should().BeFalse();
        (await _repo.AnyOtherActiveEventSettingsAsync(excludingId: null)).Should().BeTrue();
    }

    [HumansFact(Timeout = 10000)]
    public async Task GetShiftDayOffsetsForRotaAsync_ReturnsDistinctDays()
    {
        var (es, rota) = await SeedRotaAsync(RotaPeriod.Build);
        await _dbContext.Shifts.AddRangeAsync(
            NewShift(rota, dayOffset: -3),
            NewShift(rota, dayOffset: -2),
            NewShift(rota, dayOffset: -2)); // duplicate day
        await _dbContext.SaveChangesAsync();

        var days = await _repo.GetShiftDayOffsetsForRotaAsync(rota.Id);

        days.Should().BeEquivalentTo([-3, -2]);
    }

    [HumansFact]
    public async Task GetConfirmedSignupCountsByShiftAsync_EmptyInput_ReturnsEmpty()
    {
        var result = await _repo.GetConfirmedSignupCountsByShiftAsync([]);
        result.Should().BeEmpty();
    }

    [HumansFact]
    public async Task SetVolunteerTagPreferencesAsync_ReplacesExisting()
    {
        var userId = Guid.NewGuid();
        var tagA = new ShiftTag { Id = Guid.NewGuid(), Name = "A" };
        var tagB = new ShiftTag { Id = Guid.NewGuid(), Name = "B" };
        var tagC = new ShiftTag { Id = Guid.NewGuid(), Name = "C" };
        await _dbContext.ShiftTags.AddRangeAsync(tagA, tagB, tagC);
        _dbContext.VolunteerTagPreferences.Add(new VolunteerTagPreference
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ShiftTagId = tagA.Id
        });
        await _dbContext.SaveChangesAsync();

        await _repo.SetVolunteerTagPreferencesAsync(userId, [tagB.Id, tagC.Id]);

        _dbContext.ChangeTracker.Clear();
        var preferences = await _dbContext.VolunteerTagPreferences
            .AsNoTracking()
            .Where(v => v.UserId == userId)
            .Select(v => v.ShiftTagId)
            .ToListAsync();
        preferences.Should().BeEquivalentTo([tagB.Id, tagC.Id]);
    }

    // ─────────────────────────────────────────────────────────
    // Confirmed signup counts by user (cross-section read surface)
    // ─────────────────────────────────────────────────────────

    [HumansFact]
    public async Task GetConfirmedSignupCountsByUserForTeamsAsync_GroupsConfirmedAcrossRotas_ExcludesPendingAndOtherTeams()
    {
        var (es, teamId) = await SeedActiveEventWithTeamAsync();
        var rotaA = await SeedRotaInAsync(es.Id, teamId, "A");
        var rotaB = await SeedRotaInAsync(es.Id, teamId, "B");
        var shiftA = NewShift(rotaA, dayOffset: 0);
        var shiftB = NewShift(rotaB, dayOffset: 0);

        // Other team, same event — must be excluded.
        var otherTeamId = await SeedTeamAsync("other");
        var rotaOther = await SeedRotaInAsync(es.Id, otherTeamId, "O");
        var shiftOther = NewShift(rotaOther, dayOffset: 0);

        await _dbContext.Shifts.AddRangeAsync(shiftA, shiftB, shiftOther);
        await _dbContext.SaveChangesAsync();

        var user1 = Guid.NewGuid();
        var user2 = Guid.NewGuid();
        var user3 = Guid.NewGuid();

        await SeedSignupsAsync(
            (user1, shiftA, SignupStatus.Confirmed),   // counts
            (user1, shiftB, SignupStatus.Confirmed),   // counts -> user1 = 2 across rotas
            (user2, shiftA, SignupStatus.Confirmed),   // counts -> user2 = 1
            (user2, shiftA, SignupStatus.Pending),     // excluded (pending)
            (user3, shiftOther, SignupStatus.Confirmed)); // excluded (other team)

        var counts = await _repo.GetConfirmedSignupCountsByUserForTeamsAsync([teamId], es.Id);

        counts.Should().HaveCount(2);
        counts[user1].Should().Be(2);
        counts[user2].Should().Be(1);
        counts.Should().NotContainKey(user3);
    }

    [HumansFact]
    public async Task GetConfirmedSignupCountsByUserForTeamsAsync_ScopesToGivenEvent_ExcludesOtherEvents()
    {
        var (es, teamId) = await SeedActiveEventWithTeamAsync();
        var rota = await SeedRotaInAsync(es.Id, teamId, "R");
        var shift = NewShift(rota, dayOffset: 0);

        // Same team owns a rota in a different (prior) event — must be excluded.
        var otherEvent = NewEvent(isActive: false);
        _dbContext.EventSettings.Add(otherEvent);
        await _dbContext.SaveChangesAsync();
        var otherRota = await SeedRotaInAsync(otherEvent.Id, teamId, "Old");
        var otherShift = NewShift(otherRota, dayOffset: 0);

        await _dbContext.Shifts.AddRangeAsync(shift, otherShift);
        await _dbContext.SaveChangesAsync();

        var user1 = Guid.NewGuid();
        await SeedSignupsAsync(
            (user1, shift, SignupStatus.Confirmed),       // counts (active event)
            (user1, otherShift, SignupStatus.Confirmed)); // excluded (other event)

        var counts = await _repo.GetConfirmedSignupCountsByUserForTeamsAsync([teamId], es.Id);

        counts.Should().ContainKey(user1).WhoseValue.Should().Be(1);
    }

    [HumansFact]
    public async Task GetConfirmedSignupCountsByUserForRotaAsync_ScopesToSingleRota_ExcludesSiblingRotas()
    {
        var (es, teamId) = await SeedActiveEventWithTeamAsync();
        var rotaA = await SeedRotaInAsync(es.Id, teamId, "A");
        var rotaB = await SeedRotaInAsync(es.Id, teamId, "B");
        var shiftA = NewShift(rotaA, dayOffset: 0);
        var shiftB = NewShift(rotaB, dayOffset: 0);
        await _dbContext.Shifts.AddRangeAsync(shiftA, shiftB);
        await _dbContext.SaveChangesAsync();

        var user1 = Guid.NewGuid();
        var user2 = Guid.NewGuid();

        await SeedSignupsAsync(
            (user1, shiftA, SignupStatus.Confirmed),  // counts for rotaA
            (user1, shiftB, SignupStatus.Confirmed),  // sibling rota, excluded
            (user2, shiftA, SignupStatus.Pending),    // pending, excluded
            (user2, shiftB, SignupStatus.Confirmed)); // sibling rota, excluded

        var counts = await _repo.GetConfirmedSignupCountsByUserForRotaAsync(rotaA.Id);

        counts.Should().HaveCount(1);
        counts[user1].Should().Be(1);
        counts.Should().NotContainKey(user2);
    }

    [HumansFact]
    public async Task GetRotaTargetCoreAsync_ReturnsIdNameTeam_OrNull()
    {
        var (es, teamId) = await SeedActiveEventWithTeamAsync();
        var rota = await SeedRotaInAsync(es.Id, teamId, "Gate");

        var core = await _repo.GetRotaTargetCoreAsync(rota.Id);

        core.Should().NotBeNull();
        core!.Value.RotaId.Should().Be(rota.Id);
        core.Value.RotaName.Should().Be("Gate");
        core.Value.TeamId.Should().Be(teamId);

        (await _repo.GetRotaTargetCoreAsync(Guid.NewGuid())).Should().BeNull();
    }

    // ─────────────────────────────────────────────────────────
    // helpers
    // ─────────────────────────────────────────────────────────

    private async Task<(EventSettings es, Guid teamId)> SeedActiveEventWithTeamAsync()
    {
        var es = NewEvent(isActive: true);
        _dbContext.EventSettings.Add(es);
        await _dbContext.SaveChangesAsync();
        var teamId = await SeedTeamAsync("dept");
        return (es, teamId);
    }

    private async Task<Guid> SeedTeamAsync(string slug)
    {
        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = slug,
            Slug = slug,
            SystemTeamType = SystemTeamType.None,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        };
        _dbContext.Teams.Add(team);
        await _dbContext.SaveChangesAsync();
        return team.Id;
    }

    private async Task<Rota> SeedRotaInAsync(Guid eventSettingsId, Guid teamId, string name)
    {
        var rota = new Rota
        {
            Id = Guid.NewGuid(),
            EventSettingsId = eventSettingsId,
            TeamId = teamId,
            Name = name,
            Priority = ShiftPriority.Normal,
            Policy = SignupPolicy.Public,
            Period = RotaPeriod.Event,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        };
        _dbContext.Rotas.Add(rota);
        await _dbContext.SaveChangesAsync();
        return rota;
    }

    private async Task SeedSignupsAsync(params (Guid userId, Shift shift, SignupStatus status)[] rows)
    {
        foreach (var (userId, shift, status) in rows)
        {
            _dbContext.ShiftSignups.Add(new ShiftSignup
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                ShiftId = shift.Id,
                Status = status,
                CreatedAt = TestNow,
                UpdatedAt = TestNow
            });
        }
        await _dbContext.SaveChangesAsync();
    }

    private EventSettings NewEvent(bool isActive) => new()
    {
        Id = Guid.NewGuid(),
        EventName = "Event",
        TimeZoneId = "Europe/Madrid",
        GateOpeningDate = new LocalDate(2026, 7, 1),
        BuildStartOffset = -14,
        EventEndOffset = 6,
        StrikeEndOffset = 9,
        IsActive = isActive,
        CreatedAt = TestNow,
        UpdatedAt = TestNow
    };

    private async Task<(EventSettings es, Rota rota)> SeedRotaAsync(RotaPeriod period)
    {
        var es = NewEvent(isActive: true);
        _dbContext.EventSettings.Add(es);

        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = "Dept",
            Slug = "dept",
            SystemTeamType = SystemTeamType.None,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        };
        _dbContext.Teams.Add(team);

        var rota = new Rota
        {
            Id = Guid.NewGuid(),
            EventSettingsId = es.Id,
            TeamId = team.Id,
            Name = "Rota",
            Priority = ShiftPriority.Normal,
            Policy = SignupPolicy.Public,
            Period = period,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        };
        _dbContext.Rotas.Add(rota);
        await _dbContext.SaveChangesAsync();
        return (es, rota);
    }

    private Shift NewShift(Rota rota, int dayOffset) => new()
    {
        Id = Guid.NewGuid(),
        RotaId = rota.Id,
        DayOffset = dayOffset,
        IsAllDay = true,
        // StartTime/Duration are don't-care for IsAllDay rows; store midnight/24h sentinel.
        StartTime = LocalTime.Midnight,
        Duration = Duration.FromHours(24),
        MinVolunteers = 1,
        MaxVolunteers = 5,
        CreatedAt = TestNow,
        UpdatedAt = TestNow
    };
}
