using AwesomeAssertions;
using Humans.Application.Tests.Infrastructure;
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
/// Smoke tests for <see cref="ShiftManagementRepository"/>, the EF-backed
/// repository introduced in issue #541a. Covers the repository primitives
/// most likely to regress: the narrow-field pending-signup count, the
/// active-event lookup, and the tag reconcile.
/// </summary>
public sealed class ShiftManagementRepositoryTests : IDisposable
{
    private static readonly Instant TestNow = Instant.FromUtc(2026, 4, 1, 12, 0);

    private readonly HumansDbContext _dbContext;
    private readonly ShiftManagementRepository _repo;
    private readonly FakeClock _clock = new(TestNow);

    public ShiftManagementRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new HumansDbContext(options);
        _repo = new ShiftManagementRepository(new TestDbContextFactory(options));
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task GetActiveEventSettingsAsync_ReturnsActive_IgnoresInactive()
    {
        var active = NewEvent(isActive: true);
        var inactive = NewEvent(isActive: false);
        await _dbContext.EventSettings.AddRangeAsync(active, inactive);
        await _dbContext.SaveChangesAsync();

        var result = await _repo.GetActiveEventSettingsAsync();

        result.Should().NotBeNull();
        result!.Id.Should().Be(active.Id);
    }

    [Fact]
    public async Task AnyOtherActiveEventSettingsAsync_ExcludesGivenId()
    {
        var es = NewEvent(isActive: true);
        _dbContext.EventSettings.Add(es);
        await _dbContext.SaveChangesAsync();

        (await _repo.AnyOtherActiveEventSettingsAsync(excludingId: es.Id)).Should().BeFalse();
        (await _repo.AnyOtherActiveEventSettingsAsync(excludingId: null)).Should().BeTrue();
    }

    [Fact]
    public async Task GetShiftDayOffsetsForRotaAsync_ReturnsDistinctDays()
    {
        var (es, rota) = await SeedRotaAsync(RotaPeriod.Build);
        await _dbContext.Shifts.AddRangeAsync(
            NewShift(rota, dayOffset: -3),
            NewShift(rota, dayOffset: -2),
            NewShift(rota, dayOffset: -2)); // duplicate day
        await _dbContext.SaveChangesAsync();

        var days = await _repo.GetShiftDayOffsetsForRotaAsync(rota.Id);

        days.Should().BeEquivalentTo(new[] { -3, -2 });
    }

    [Fact]
    public async Task GetConfirmedSignupCountsByShiftAsync_EmptyInput_ReturnsEmpty()
    {
        var result = await _repo.GetConfirmedSignupCountsByShiftAsync(Array.Empty<Guid>());
        result.Should().BeEmpty();
    }

    [Fact]
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

        await _repo.SetVolunteerTagPreferencesAsync(userId, new[] { tagB.Id, tagC.Id });

        _dbContext.ChangeTracker.Clear();
        var preferences = await _dbContext.VolunteerTagPreferences
            .AsNoTracking()
            .Where(v => v.UserId == userId)
            .Select(v => v.ShiftTagId)
            .ToListAsync();
        preferences.Should().BeEquivalentTo(new[] { tagB.Id, tagC.Id });
    }

    [Fact]
    public async Task GetActiveEventIdAsync_ReturnsEmpty_WhenEventInactive()
    {
        var es = NewEvent(isActive: false);
        _dbContext.EventSettings.Add(es);
        await _dbContext.SaveChangesAsync();

        var result = await _repo.GetActiveEventIdAsync(es.Id);
        result.Should().Be(Guid.Empty);
    }

    // ─────────────────────────────────────────────────────────
    // helpers
    // ─────────────────────────────────────────────────────────

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
        StartTime = new LocalTime(0, 0),
        Duration = Duration.FromHours(24),
        MinVolunteers = 1,
        MaxVolunteers = 5,
        CreatedAt = TestNow,
        UpdatedAt = TestNow
    };
}
