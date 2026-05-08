using AwesomeAssertions;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories.Shifts;
using Humans.Integration.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Xunit;

namespace Humans.Integration.Tests.Repositories.Shifts;

/// <summary>
/// Integration tests for <see cref="VolunteerTrackingRepository"/>. Mirrors the
/// repo's established service-test shape (e.g. <c>CalendarServiceTests</c>):
/// uses <see cref="IClassFixture{T}"/> for the test-container-backed factory,
/// resolves the Scoped <see cref="HumansDbContext"/> per test through a DI
/// scope, and exercises the repository against a real PostgreSQL container.
///
/// <see cref="IntegrationTestBase"/> is HttpClient-only, so it doesn't fit
/// repository tests; we use the factory directly per the
/// <c>CalendarServiceTests</c> pattern.
/// </summary>
public class VolunteerTrackingRepositoryTests : IClassFixture<HumansWebApplicationFactory>
{
    private readonly HumansWebApplicationFactory _factory;

    public VolunteerTrackingRepositoryTests(HumansWebApplicationFactory factory) =>
        _factory = factory;

    [HumansFact]
    public async Task GetAsync_returns_null_when_no_row_exists()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HumansDbContext>();
        var sut = new VolunteerTrackingRepository(db);

        var result = await sut.GetAsync(Guid.NewGuid(), Guid.NewGuid());

        result.Should().BeNull();
    }

    [HumansFact]
    public async Task UpsertCampSetupAsync_inserts_when_no_row_exists()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HumansDbContext>();
        var es = await SeedActiveEventAsync(db);
        var userId = Guid.NewGuid();
        var sut = new VolunteerTrackingRepository(db);

        var result = await sut.UpsertCampSetupAsync(
            userId, es.Id,
            barrioSetupStartDate: new LocalDate(2026, 7, 1),
            notes: "left for barrio",
            setByUserId: Guid.NewGuid(),
            setAt: SystemClock.Instance.GetCurrentInstant());

        result.UserId.Should().Be(userId);
        result.BarrioSetupStartDate.Should().Be(new LocalDate(2026, 7, 1));
        result.Notes.Should().Be("left for barrio");

        var fetched = await sut.GetAsync(userId, es.Id);
        fetched.Should().NotBeNull();
        fetched!.Id.Should().Be(result.Id);
    }

    [HumansFact]
    public async Task ReplaceBlockedDaysAsync_persists_sorted_deduped_list_and_returns_prior()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HumansDbContext>();
        var es = await SeedActiveEventAsync(db);
        var userId = Guid.NewGuid();
        var sut = new VolunteerTrackingRepository(db);

        // Seed an empty row first.
        await sut.UpsertCampSetupAsync(userId, es.Id, null, null, null, null);

        var prior = await sut.ReplaceBlockedDaysAsync(userId, es.Id, new[] { -3, -5, -3 });
        prior.Should().BeEmpty();

        var row = await sut.GetAsync(userId, es.Id);
        row.Should().NotBeNull();
        row!.BlockedDayOffsets.Should().Equal(-5, -3);   // sorted ascending, deduped

        var prior2 = await sut.ReplaceBlockedDaysAsync(userId, es.Id, new[] { -7 });
        prior2.Should().Equal(-5, -3);
    }

    [HumansFact]
    public async Task UpsertCampSetupAsync_updates_existing_row_and_preserves_blocked_days()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HumansDbContext>();
        var es = await SeedActiveEventAsync(db);
        var userId = Guid.NewGuid();
        var sut = new VolunteerTrackingRepository(db);

        await sut.UpsertCampSetupAsync(userId, es.Id, new LocalDate(2026, 6, 30), "first", null, null);
        await sut.ReplaceBlockedDaysAsync(userId, es.Id, new[] { -3 });
        await sut.UpsertCampSetupAsync(userId, es.Id, new LocalDate(2026, 7, 1), "second", null, null);

        var rows = await db.VolunteerBuildStatuses
            .Where(x => x.UserId == userId && x.EventSettingsId == es.Id)
            .ToListAsync();
        rows.Should().HaveCount(1);
        rows[0].BarrioSetupStartDate.Should().Be(new LocalDate(2026, 7, 1));
        rows[0].Notes.Should().Be("second");
        rows[0].BlockedDayOffsets.Should().Equal(-3);   // preserved
    }

    /// <summary>
    /// Seeds a fresh <see cref="EventSettings"/> row with a unique name so each
    /// test gets an isolated event id (the test container is shared across
    /// tests in the fixture).
    /// </summary>
    private static async Task<EventSettings> SeedActiveEventAsync(HumansDbContext db)
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        var es = new EventSettings
        {
            Id = Guid.NewGuid(),
            EventName = $"VTrack-{Guid.NewGuid():N}",
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
        await db.SaveChangesAsync();
        return es;
    }
}
