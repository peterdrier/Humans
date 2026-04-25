using AwesomeAssertions;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories.CitiPlanning;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Testing;
using Xunit;

namespace Humans.Application.Tests.Repositories;

public sealed class CityPlanningRepositoryTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly CityPlanningRepository _repo;

    public CityPlanningRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new HumansDbContext(options);
        _clock = new FakeClock(Instant.FromUtc(2026, 3, 1, 12, 0));
        _repo = new CityPlanningRepository(new TestDbContextFactory(options));
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    // ==========================================================================
    // SavePolygonAndAppendHistoryAsync
    // ==========================================================================

    [HumansFact]
    public async Task SavePolygonAndAppendHistoryAsync_FirstCall_CreatesPolygonAndHistory()
    {
        var campSeasonId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var now = _clock.GetCurrentInstant();

        var (polygon, history) = await _repo.SavePolygonAndAppendHistoryAsync(
            campSeasonId, """{"type":"Feature"}""", 100.0, userId, "Saved", now);

        polygon.CampSeasonId.Should().Be(campSeasonId);
        polygon.AreaSqm.Should().Be(100.0);
        history.Note.Should().Be("Saved");
        history.CampSeasonId.Should().Be(campSeasonId);

        (await _dbContext.CampPolygons.AsNoTracking().CountAsync()).Should().Be(1);
        (await _dbContext.CampPolygonHistories.AsNoTracking().CountAsync()).Should().Be(1);
    }

    [HumansFact]
    public async Task SavePolygonAndAppendHistoryAsync_SecondCall_UpdatesPolygonAppendsHistory()
    {
        var campSeasonId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await _repo.SavePolygonAndAppendHistoryAsync(
            campSeasonId, """{"type":"Feature","v":1}""", 100.0, userId, "Saved", _clock.GetCurrentInstant());
        _clock.Advance(Duration.FromSeconds(1));
        await _repo.SavePolygonAndAppendHistoryAsync(
            campSeasonId, """{"type":"Feature","v":2}""", 200.0, userId, "Saved", _clock.GetCurrentInstant());

        (await _dbContext.CampPolygons.AsNoTracking().CountAsync(p => p.CampSeasonId == campSeasonId))
            .Should().Be(1);
        (await _dbContext.CampPolygonHistories.AsNoTracking().CountAsync(h => h.CampSeasonId == campSeasonId))
            .Should().Be(2);

        var polygon = await _dbContext.CampPolygons.AsNoTracking().SingleAsync();
        polygon.AreaSqm.Should().Be(200.0);
    }

    // ==========================================================================
    // Read operations
    // ==========================================================================

    [HumansFact]
    public async Task GetPolygonsByCampSeasonIdsAsync_ReturnsMatchingRowsOnly()
    {
        var matching = Guid.NewGuid();
        var other = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await _repo.SavePolygonAndAppendHistoryAsync(
            matching, """{}""", 100.0, userId, "Saved", _clock.GetCurrentInstant());
        await _repo.SavePolygonAndAppendHistoryAsync(
            other, """{}""", 200.0, userId, "Saved", _clock.GetCurrentInstant());

        var result = await _repo.GetPolygonsByCampSeasonIdsAsync([matching]);

        result.Should().HaveCount(1);
        result[0].CampSeasonId.Should().Be(matching);
    }

    [HumansFact]
    public async Task GetPolygonsByCampSeasonIdsAsync_EmptyInput_ReturnsEmpty()
    {
        var result = await _repo.GetPolygonsByCampSeasonIdsAsync([]);
        result.Should().BeEmpty();
    }

    [HumansFact]
    public async Task GetCampSeasonIdsWithPolygonAsync_ReturnsMatchingIdsOnly()
    {
        var withPolygon = Guid.NewGuid();
        var withoutPolygon = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await _repo.SavePolygonAndAppendHistoryAsync(
            withPolygon, """{}""", 100.0, userId, "Saved", _clock.GetCurrentInstant());

        var result = await _repo.GetCampSeasonIdsWithPolygonAsync([withPolygon, withoutPolygon]);

        result.Should().ContainSingle().Which.Should().Be(withPolygon);
    }

    [HumansFact]
    public async Task GetHistoryForCampSeasonAsync_ReturnsDescendingByModifiedAt()
    {
        var campSeasonId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await _repo.SavePolygonAndAppendHistoryAsync(
            campSeasonId, """{}""", 100.0, userId, "Saved", _clock.GetCurrentInstant());
        _clock.Advance(Duration.FromSeconds(1));
        await _repo.SavePolygonAndAppendHistoryAsync(
            campSeasonId, """{}""", 200.0, userId, "Saved", _clock.GetCurrentInstant());

        var result = await _repo.GetHistoryForCampSeasonAsync(campSeasonId);

        result.Should().HaveCount(2);
        result[0].AreaSqm.Should().Be(200.0);
        result[1].AreaSqm.Should().Be(100.0);
    }

    [HumansFact]
    public async Task GetHistoryEntryAsync_ReturnsNull_WhenNotMatching()
    {
        var result = await _repo.GetHistoryEntryAsync(Guid.NewGuid(), Guid.NewGuid());
        result.Should().BeNull();
    }

    [HumansFact]
    public async Task GetHistoryEntryAsync_DoesNotCrossCampSeasonId()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var (_, historyA) = await _repo.SavePolygonAndAppendHistoryAsync(
            a, """{}""", 100.0, userId, "Saved", _clock.GetCurrentInstant());

        // Wrong campSeasonId should not return the row.
        var result = await _repo.GetHistoryEntryAsync(b, historyA.Id);

        result.Should().BeNull();
    }

    // ==========================================================================
    // CityPlanningSettings operations
    // ==========================================================================

    [HumansFact]
    public async Task GetSettingsByYearAsync_ReturnsNull_WhenNotFound()
    {
        var result = await _repo.GetSettingsByYearAsync(2030);
        result.Should().BeNull();
    }

    [HumansFact]
    public async Task GetOrCreateSettingsAsync_CreatesRow_WhenNotFound()
    {
        var now = _clock.GetCurrentInstant();

        var result = await _repo.GetOrCreateSettingsAsync(2027, now);

        result.Year.Should().Be(2027);
        result.IsPlacementOpen.Should().BeFalse();
        result.UpdatedAt.Should().Be(now);
        (await _dbContext.CityPlanningSettings.AsNoTracking().CountAsync(s => s.Year == 2027))
            .Should().Be(1);
    }

    [HumansFact]
    public async Task GetOrCreateSettingsAsync_IsIdempotent()
    {
        var now = _clock.GetCurrentInstant();
        await _repo.GetOrCreateSettingsAsync(2027, now);
        await _repo.GetOrCreateSettingsAsync(2027, now);

        (await _dbContext.CityPlanningSettings.AsNoTracking().CountAsync(s => s.Year == 2027))
            .Should().Be(1);
    }

    [HumansFact]
    public async Task MutateSettingsAsync_CreatesRow_WhenMissing()
    {
        var now = _clock.GetCurrentInstant();

        var result = await _repo.MutateSettingsAsync(2028, s => s.IsPlacementOpen = true, now);

        result.IsPlacementOpen.Should().BeTrue();
        result.UpdatedAt.Should().Be(now);
    }

    [HumansFact]
    public async Task MutateSettingsAsync_AppliesChange_AndSetsUpdatedAt()
    {
        _dbContext.CityPlanningSettings.Add(new CityPlanningSettings
        {
            Year = 2026,
            IsPlacementOpen = false,
            UpdatedAt = _clock.GetCurrentInstant()
        });
        await _dbContext.SaveChangesAsync();

        _clock.Advance(Duration.FromSeconds(10));
        var now = _clock.GetCurrentInstant();

        var result = await _repo.MutateSettingsAsync(
            2026,
            s =>
            {
                s.IsPlacementOpen = true;
                s.OpenedAt = now;
            },
            now);

        result.IsPlacementOpen.Should().BeTrue();
        result.OpenedAt.Should().Be(now);
        result.UpdatedAt.Should().Be(now);
    }
}
