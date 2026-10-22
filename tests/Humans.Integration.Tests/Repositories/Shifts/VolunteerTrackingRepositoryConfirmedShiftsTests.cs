using Humans.Shifts.Data;
using Humans.Shifts.Domain;
using Humans.Settings.Contracts;
using Humans.Teams.Data;
using Humans.Teams.Domain;
using AwesomeAssertions;
using Humans.Integration.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Xunit;
using Humans.Users.Contracts;
using Humans.Users.Data;

using Humans.Shifts.Contracts;
namespace Humans.Integration.Tests.Repositories.Shifts;

/// <summary><see cref="IShiftManagementRepository.GetConfirmedShiftsInRangeAsync"/> against real PostgreSQL.</summary>
public sealed class VolunteerTrackingRepositoryConfirmedShiftsTests(HumansTestDatabase database)
    : IntegrationTestBase(database)
{
    [HumansFact]
    public async Task ReturnsOnlyConfirmedSignups()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<UsersDbContext>();
        var teamsDb = scope.ServiceProvider.GetRequiredService<TeamsDbContext>();
        var shiftsDb = scope.ServiceProvider.GetRequiredService<ShiftsDbContext>();
        var repo = scope.ServiceProvider.GetRequiredService<IShiftManagementRepository>();

        var (eventId, calendar, _, _, _) = await SeedFixtureAsync(db, teamsDb, shiftsDb);

        var start = new LocalDate(2026, 7, 7);
        var end = new LocalDate(2026, 7, 12);

        var rows = await repo.GetConfirmedShiftsInRangeAsync(eventId, calendar, start, end, departmentId: null, ct: TestContext.Current.CancellationToken);

        rows.Should().OnlyContain(r => r.UserId != Guid.Empty);
        // The seed has two Confirmed signups plus a Pending and a Cancelled; only the Confirmed appear.
        rows.Should().HaveCount(2);
    }

    [HumansFact]
    public async Task ExcludesShiftsOutsideRange()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<UsersDbContext>();
        var teamsDb = scope.ServiceProvider.GetRequiredService<TeamsDbContext>();
        var shiftsDb = scope.ServiceProvider.GetRequiredService<ShiftsDbContext>();
        var repo = scope.ServiceProvider.GetRequiredService<IShiftManagementRepository>();

        var (eventId, calendar, _, _, _) = await SeedFixtureAsync(db, teamsDb, shiftsDb);

        var rows = await repo.GetConfirmedShiftsInRangeAsync(
            eventId,
            calendar,
            new LocalDate(2026, 1, 1),
            new LocalDate(2026, 1, 2),
            departmentId: null,
            ct: TestContext.Current.CancellationToken);

        rows.Should().BeEmpty();
    }

    [HumansFact]
    public async Task DepartmentFilter_ExcludesOtherTeams()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<UsersDbContext>();
        var teamsDb = scope.ServiceProvider.GetRequiredService<TeamsDbContext>();
        var shiftsDb = scope.ServiceProvider.GetRequiredService<ShiftsDbContext>();
        var repo = scope.ServiceProvider.GetRequiredService<IShiftManagementRepository>();

        var (eventId, calendar, teamAId, teamBId, _) = await SeedFixtureAsync(db, teamsDb, shiftsDb);

        var start = new LocalDate(2026, 7, 7);
        var end = new LocalDate(2026, 7, 12);

        var teamARows = await repo.GetConfirmedShiftsInRangeAsync(eventId, calendar, start, end, departmentId: teamAId, ct: TestContext.Current.CancellationToken);
        var teamBRows = await repo.GetConfirmedShiftsInRangeAsync(eventId, calendar, start, end, departmentId: teamBId, ct: TestContext.Current.CancellationToken);

        teamARows.Should().OnlyContain(r => r.TeamId == teamAId);
        teamBRows.Should().OnlyContain(r => r.TeamId == teamBId);
    }

    [HumansFact]
    public async Task ShiftThatOverlapsStartBoundary_IsIncluded()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<UsersDbContext>();
        var teamsDb = scope.ServiceProvider.GetRequiredService<TeamsDbContext>();
        var shiftsDb = scope.ServiceProvider.GetRequiredService<ShiftsDbContext>();
        var repo = scope.ServiceProvider.GetRequiredService<IShiftManagementRepository>();

        var (eventId, calendar, _, _, _) = await SeedFixtureAsync(db, teamsDb, shiftsDb);

        // A confirmed shift starts on the morning of 2026-07-07; a range starting that day must include it.
        var rows = await repo.GetConfirmedShiftsInRangeAsync(
            eventId,
            calendar,
            new LocalDate(2026, 7, 7),
            new LocalDate(2026, 7, 7),
            departmentId: null,
            ct: TestContext.Current.CancellationToken);

        rows.Should().NotBeEmpty();
    }

    /// <summary>
    /// One event (gate opens 2026-07-01), teams A and B with a rota each, and shifts on 7/7 (A), 7/8 (B) and 7/9 (A).
    /// Signups: A 7/7 Confirmed and Pending, A 7/9 Cancelled, B 7/8 Confirmed.
    /// </summary>
    private static async Task<(Guid eventId, EventSettingsInfo calendar, Guid teamAId, Guid teamBId, Guid userId)> SeedFixtureAsync(UsersDbContext db, TeamsDbContext teamsDb, ShiftsDbContext shiftsDb)
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
        shiftsDb.EventSettings.Add(es);

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
        teamsDb.Teams.Add(teamA);
        teamsDb.Teams.Add(teamB);

        var rotaA = NewRota(es.Id, teamA.Id, now, $"RotaA-{suffix}");
        var rotaB = NewRota(es.Id, teamB.Id, now, $"RotaB-{suffix}");
        shiftsDb.Rotas.Add(rotaA);
        shiftsDb.Rotas.Add(rotaB);

        var shiftA77 = NewShift(rotaA.Id, dayOffset: 6, now);   // 2026-07-07
        var shiftA79 = NewShift(rotaA.Id, dayOffset: 8, now);   // 2026-07-09
        var shiftB78 = NewShift(rotaB.Id, dayOffset: 7, now);   // 2026-07-08
        shiftsDb.Shifts.Add(shiftA77);
        shiftsDb.Shifts.Add(shiftA79);
        shiftsDb.Shifts.Add(shiftB78);

        var user1 = NewUser(now, suffix + "u1");
        var user2 = NewUser(now, suffix + "u2");
        db.Users.Add(user1);
        db.Users.Add(user2);

        shiftsDb.ShiftSignups.Add(NewSignup(user1.Id, shiftA77.Id, SignupStatus.Confirmed, now));
        shiftsDb.ShiftSignups.Add(NewSignup(user2.Id, shiftA77.Id, SignupStatus.Pending, now));
        shiftsDb.ShiftSignups.Add(NewSignup(user1.Id, shiftA79.Id, SignupStatus.Cancelled, now));
        shiftsDb.ShiftSignups.Add(NewSignup(user1.Id, shiftB78.Id, SignupStatus.Confirmed, now));

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await teamsDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        await shiftsDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        var calendar = new EventSettingsInfo(
            Id: es.Id,
            EventName: es.EventName,
            Year: es.Year,
            TimeZoneId: es.TimeZoneId,
            GateOpeningDate: es.GateOpeningDate,
            BuildStartOffset: es.BuildStartOffset,
            EventEndOffset: es.EventEndOffset,
            StrikeEndOffset: es.StrikeEndOffset,
            FirstCrewStartOffset: es.FirstCrewStartOffset,
            SetupWeekStartOffset: es.SetupWeekStartOffset,
            PreEventWeekStartOffset: es.PreEventWeekStartOffset,
            FinishingWeekendStartOffset: es.FinishingWeekendStartOffset,
            EarlyEntryCapacity: new Dictionary<int, int>(es.EarlyEntryCapacity),
            BarriosEarlyEntryAllocation: es.BarriosEarlyEntryAllocation is null
                ? null : new Dictionary<int, int>(es.BarriosEarlyEntryAllocation),
            EarlyEntryClose: es.EarlyEntryClose);
        return (es.Id, calendar, teamA.Id, teamB.Id, user1.Id);
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
