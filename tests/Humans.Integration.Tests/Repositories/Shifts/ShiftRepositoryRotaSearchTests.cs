using AwesomeAssertions;
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
/// The admin-only-rota half of the global-search privacy guarantee. The filter is a
/// Postgres <c>ILike</c> predicate, so it cannot be exercised against the in-memory
/// provider the Application tests use — it needs the run's real PostgreSQL container.
///
/// <para>
/// Per the ruling on nobodies-collective/Humans#985, a text query is still filtered:
/// a rota with <c>IsVisibleToVolunteers = false</c> must never match by name for anyone.
/// The by-GUID counterpart (which deliberately skips this filter) is pinned in
/// <c>ShiftManagementServiceTests.SearchAsync_GuidQuery_ResolvesARotaHiddenFromVolunteers</c>.
/// </para>
/// </summary>
public class ShiftRepositoryRotaSearchTests(HumansTestDatabase database)
    : IntegrationTestBase(database)
{
    [HumansFact]
    public async Task SearchVolunteerVisibleRotasAsync_ExcludesRotasHiddenFromVolunteers()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamsDbContext>();
        var shiftsDb = scope.ServiceProvider.GetRequiredService<ShiftsDbContext>();
        var sut = scope.ServiceProvider.GetRequiredService<IShiftManagementRepository>();

        var es = await SeedActiveEventAsync(shiftsDb);
        var team = await SeedTeamAsync(db);
        var token = $"Kitchen{Guid.NewGuid():N}";
        await SeedRotaAsync(shiftsDb, es, team, $"{token} Prep", visibleToVolunteers: true);
        await SeedRotaAsync(shiftsDb, es, team, $"{token} Admin", visibleToVolunteers: false);

        var hits = await sut.SearchVolunteerVisibleRotasAsync(
            token, es.Id, int.MaxValue, TestContext.Current.CancellationToken);

        hits.Select(r => r.Name).Should().Equal($"{token} Prep");
    }

    [HumansFact]
    public async Task SearchVolunteerVisibleRotasAsync_MatchesCaseInsensitively_WithinTheGivenEventOnly()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TeamsDbContext>();
        var shiftsDb = scope.ServiceProvider.GetRequiredService<ShiftsDbContext>();
        var sut = scope.ServiceProvider.GetRequiredService<IShiftManagementRepository>();

        var es = await SeedActiveEventAsync(shiftsDb);
        var otherEvent = await SeedActiveEventAsync(shiftsDb);
        var team = await SeedTeamAsync(db);
        var token = $"Gate{Guid.NewGuid():N}";
        await SeedRotaAsync(shiftsDb, es, team, $"{token} Crew", visibleToVolunteers: true);
        await SeedRotaAsync(shiftsDb, otherEvent, team, $"{token} Crew", visibleToVolunteers: true);

        var hits = await sut.SearchVolunteerVisibleRotasAsync(
            token.ToUpperInvariant(), es.Id, int.MaxValue, TestContext.Current.CancellationToken);

        hits.Should().ContainSingle().Which.EventSettingsId.Should().Be(es.Id);
    }

    private static async Task<EventSettings> SeedActiveEventAsync(ShiftsDbContext shiftsDb)
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        var es = new EventSettings
        {
            Id = Guid.NewGuid(),
            EventName = $"RotaSearch-{Guid.NewGuid():N}",
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
        await shiftsDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        return es;
    }

    private static async Task<Team> SeedTeamAsync(TeamsDbContext db)
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = $"RotaSearch Team {Guid.NewGuid():N}",
            Slug = $"rotasearch-{Guid.NewGuid():N}",
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Teams.Add(team);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return team;
    }

    private static async Task SeedRotaAsync(
        ShiftsDbContext shiftsDb, EventSettings es, Team team, string name, bool visibleToVolunteers)
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        shiftsDb.Rotas.Add(new Rota
        {
            Id = Guid.NewGuid(),
            EventSettingsId = es.Id,
            TeamId = team.Id,
            Name = name,
            Priority = ShiftPriority.Normal,
            Policy = SignupPolicy.Public,
            Period = RotaPeriod.Event,
            IsVisibleToVolunteers = visibleToVolunteers,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await shiftsDb.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}
