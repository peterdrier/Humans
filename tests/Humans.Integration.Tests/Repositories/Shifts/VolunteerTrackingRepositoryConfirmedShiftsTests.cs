using AwesomeAssertions;
using Humans.Application.DTOs.VolunteerTrackingExport;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Integration.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Xunit;

namespace Humans.Integration.Tests.Repositories.Shifts;

/// <summary>
/// Integration tests for
/// <see cref="IVolunteerTrackingRepository.GetConfirmedShiftsInRangeAsync"/>.
/// Mirrors the existing <c>VolunteerTrackingRepositoryTests</c> style: container-
/// backed factory via <see cref="IClassFixture{T}"/>, scope per test, real
/// PostgreSQL. Seeding helpers are inlined here (rather than shared) because the
/// existing class keeps them <c>private</c>.
/// </summary>
public sealed class VolunteerTrackingRepositoryConfirmedShiftsTests
    : IClassFixture<HumansWebApplicationFactory>
{
    private readonly HumansWebApplicationFactory _factory;

    public VolunteerTrackingRepositoryConfirmedShiftsTests(HumansWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [HumansFact]
    public async Task ReturnsOnlyConfirmedSignups()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HumansDbContext>();
        var repo = scope.ServiceProvider.GetRequiredService<IVolunteerTrackingRepository>();

        var (eventId, _, _, _) = await SeedFixtureAsync(db);

        var start = new LocalDate(2026, 7, 7);
        var end = new LocalDate(2026, 7, 12);

        var rows = await repo.GetConfirmedShiftsInRangeAsync(eventId, start, end, departmentId: null, ct: default);

        rows.Should().OnlyContain(r => r.UserId != Guid.Empty);
        // The seed creates 2 Confirmed signups total (one TeamA, one TeamB), plus a Pending
        // and a Cancelled on TeamA shifts. Only the 2 Confirmed should appear.
        rows.Should().HaveCount(2);
    }

    [HumansFact]
    public async Task ExcludesShiftsOutsideRange()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HumansDbContext>();
        var repo = scope.ServiceProvider.GetRequiredService<IVolunteerTrackingRepository>();

        var (eventId, _, _, _) = await SeedFixtureAsync(db);

        // Range entirely before the seeded shifts.
        var rows = await repo.GetConfirmedShiftsInRangeAsync(
            eventId,
            new LocalDate(2026, 1, 1),
            new LocalDate(2026, 1, 2),
            departmentId: null,
            ct: default);

        rows.Should().BeEmpty();
    }

    [HumansFact]
    public async Task DepartmentFilter_ExcludesOtherTeams()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HumansDbContext>();
        var repo = scope.ServiceProvider.GetRequiredService<IVolunteerTrackingRepository>();

        var (eventId, teamAId, teamBId, _) = await SeedFixtureAsync(db);

        var start = new LocalDate(2026, 7, 7);
        var end = new LocalDate(2026, 7, 12);

        var teamARows = await repo.GetConfirmedShiftsInRangeAsync(eventId, start, end, departmentId: teamAId, ct: default);
        var teamBRows = await repo.GetConfirmedShiftsInRangeAsync(eventId, start, end, departmentId: teamBId, ct: default);

        teamARows.Should().OnlyContain(r => r.TeamId == teamAId);
        teamBRows.Should().OnlyContain(r => r.TeamId == teamBId);
    }

    [HumansFact]
    public async Task ShiftThatOverlapsStartBoundary_IsIncluded()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HumansDbContext>();
        var repo = scope.ServiceProvider.GetRequiredService<IVolunteerTrackingRepository>();

        var (eventId, _, _, _) = await SeedFixtureAsync(db);

        // The seed includes a confirmed shift that starts on 2026-07-07 morning.
        // A range starting that same day must include it.
        var rows = await repo.GetConfirmedShiftsInRangeAsync(
            eventId,
            new LocalDate(2026, 7, 7),
            new LocalDate(2026, 7, 7),
            departmentId: null,
            ct: default);

        rows.Should().NotBeEmpty();
    }

    /// <summary>
    /// Seeds a minimal fixture:
    /// - One EventSettings ("Elsewhere 2026", Europe/Madrid, GateOpeningDate 2026-07-01).
    /// - Two teams: TeamA, TeamB. One rota per team.
    /// - Three shifts on 2026-07-07 (TeamA, DayOffset 6), 2026-07-08 (TeamB, DayOffset 7),
    ///   2026-07-09 (TeamA, DayOffset 8).
    /// - Three signups on the TeamA shifts: one Confirmed (the 7/7 shift), one Pending
    ///   (a second user on 7/7), one Cancelled (the 7/9 shift).
    /// - One Confirmed signup on the TeamB shift (7/8).
    /// </summary>
    private static async Task<(Guid eventId, Guid teamAId, Guid teamBId, Guid userId)> SeedFixtureAsync(HumansDbContext db)
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        var suffix = Guid.NewGuid().ToString("N");

        var es = new EventSettings
        {
            Id = Guid.NewGuid(),
            EventName = $"Elsewhere-{suffix}",
            Year = 2026,
            TimeZoneId = "Europe/Madrid",
            GateOpeningDate = new LocalDate(2026, 7, 1),
            BuildStartOffset = -10,
            EventEndOffset = 6,
            StrikeEndOffset = 8,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.EventSettings.Add(es);

        var teamA = new Team
        {
            Id = Guid.NewGuid(),
            Name = $"TeamA-{suffix}",
            Slug = $"teama-{suffix}",
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var teamB = new Team
        {
            Id = Guid.NewGuid(),
            Name = $"TeamB-{suffix}",
            Slug = $"teamb-{suffix}",
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Teams.Add(teamA);
        db.Teams.Add(teamB);

        var rotaA = NewRota(es.Id, teamA.Id, now, $"RotaA-{suffix}");
        var rotaB = NewRota(es.Id, teamB.Id, now, $"RotaB-{suffix}");
        db.Rotas.Add(rotaA);
        db.Rotas.Add(rotaB);

        // DayOffsets: GateOpeningDate = 2026-07-01, so 6 = 7/7, 7 = 7/8, 8 = 7/9.
        var shiftA77 = NewShift(rotaA.Id, dayOffset: 6, now);   // 2026-07-07
        var shiftA79 = NewShift(rotaA.Id, dayOffset: 8, now);   // 2026-07-09
        var shiftB78 = NewShift(rotaB.Id, dayOffset: 7, now);   // 2026-07-08
        db.Shifts.Add(shiftA77);
        db.Shifts.Add(shiftA79);
        db.Shifts.Add(shiftB78);

        var user1 = NewUser(now, suffix + "u1");
        var user2 = NewUser(now, suffix + "u2");
        db.Users.Add(user1);
        db.Users.Add(user2);

        // TeamA 7/7: Confirmed (user1)
        db.ShiftSignups.Add(NewSignup(user1.Id, shiftA77.Id, SignupStatus.Confirmed, now));
        // TeamA 7/7: Pending  (user2) — should be excluded
        db.ShiftSignups.Add(NewSignup(user2.Id, shiftA77.Id, SignupStatus.Pending, now));
        // TeamA 7/9: Cancelled (user1) — should be excluded
        db.ShiftSignups.Add(NewSignup(user1.Id, shiftA79.Id, SignupStatus.Cancelled, now));
        // TeamB 7/8: Confirmed (user1)
        db.ShiftSignups.Add(NewSignup(user1.Id, shiftB78.Id, SignupStatus.Confirmed, now));

        await db.SaveChangesAsync();
        return (es.Id, teamA.Id, teamB.Id, user1.Id);
    }

    private static Rota NewRota(Guid eventSettingsId, Guid teamId, Instant now, string name) => new()
    {
        Id = Guid.NewGuid(),
        EventSettingsId = eventSettingsId,
        TeamId = teamId,
        Name = name,
        Priority = ShiftPriority.Normal,
        Policy = SignupPolicy.Public,
        Period = RotaPeriod.Event,
        IsVisibleToVolunteers = true,
        CreatedAt = now,
        UpdatedAt = now,
    };

    private static Shift NewShift(Guid rotaId, int dayOffset, Instant now) => new()
    {
        Id = Guid.NewGuid(),
        RotaId = rotaId,
        DayOffset = dayOffset,
        IsAllDay = true,
        StartTime = new LocalTime(8, 0),
        Duration = Duration.FromHours(10),
        MinVolunteers = 1,
        MaxVolunteers = 10,
        CreatedAt = now,
        UpdatedAt = now,
    };

    private static User NewUser(Instant now, string suffix) => new()
    {
        Id = Guid.NewGuid(),
        UserName = $"vts-{suffix}@example.test",
        NormalizedUserName = $"VTS-{suffix}@EXAMPLE.TEST",
        DisplayName = "Confirmed Shifts Test User",
        CreatedAt = now,
    };

    private static ShiftSignup NewSignup(Guid userId, Guid shiftId, SignupStatus status, Instant now) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        ShiftId = shiftId,
        Status = status,
        CreatedAt = now,
        UpdatedAt = now,
    };
}
