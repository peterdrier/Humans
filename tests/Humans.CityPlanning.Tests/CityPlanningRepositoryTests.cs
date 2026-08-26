using AwesomeAssertions;

using Humans.CityPlanning.Domain;
using Humans.CityPlanning.Data;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Testing;

namespace Humans.CityPlanning.Tests;

public sealed class CityPlanningRepositoryTests : CityPlanningTestBase
{
    private readonly CityPlanningRepository _repo;

    public CityPlanningRepositoryTests()
    {
        _repo = new CityPlanningRepository(CityPlanningDbFactory);
    }

    // ==========================================================================
    // SavePolygonAndAppendHistoryAsync
    // ==========================================================================

    [HumansFact]
    public async Task SavePolygonAndAppendHistoryAsync_FirstCall_CreatesPolygonAndHistory()
    {
        var campSeasonId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var now = Clock.GetCurrentInstant();

        var polygon = await _repo.SavePolygonAndAppendHistoryAsync(
            campSeasonId, """{"type":"Feature"}""", 100.0, userId, "Saved", now, Xunit.TestContext.Current.CancellationToken);

        polygon.CampSeasonId.Should().Be(campSeasonId);
        polygon.AreaSqm.Should().Be(100.0);

        var history = await _repo.GetHistoryForCampSeasonAsync(campSeasonId, Xunit.TestContext.Current.CancellationToken);
        history.Should().ContainSingle();
        history[0].Note.Should().Be("Saved");
        history[0].CampSeasonId.Should().Be(campSeasonId);

        (await CityPlanningDb.CampPolygons.AsNoTracking().CountAsync(Xunit.TestContext.Current.CancellationToken)).Should().Be(1);
        (await CityPlanningDb.CampPolygonHistories.AsNoTracking().CountAsync(Xunit.TestContext.Current.CancellationToken)).Should().Be(1);
    }

    [HumansFact]
    public async Task SavePolygonAndAppendHistoryAsync_SecondCall_UpdatesPolygonAppendsHistory()
    {
        var campSeasonId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await _repo.SavePolygonAndAppendHistoryAsync(
            campSeasonId, """{"type":"Feature","v":1}""", 100.0, userId, "Saved", Clock.GetCurrentInstant(), Xunit.TestContext.Current.CancellationToken);
        Clock.Advance(Duration.FromSeconds(1));
        await _repo.SavePolygonAndAppendHistoryAsync(
            campSeasonId, """{"type":"Feature","v":2}""", 200.0, userId, "Saved", Clock.GetCurrentInstant(), Xunit.TestContext.Current.CancellationToken);

        (await CityPlanningDb.CampPolygons.AsNoTracking().CountAsync(p => p.CampSeasonId == campSeasonId, Xunit.TestContext.Current.CancellationToken))
            .Should().Be(1);
        (await CityPlanningDb.CampPolygonHistories.AsNoTracking().CountAsync(h => h.CampSeasonId == campSeasonId, Xunit.TestContext.Current.CancellationToken))
            .Should().Be(2);

        var polygon = await CityPlanningDb.CampPolygons.AsNoTracking().SingleAsync(Xunit.TestContext.Current.CancellationToken);
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
            matching, """{}""", 100.0, userId, "Saved", Clock.GetCurrentInstant(), Xunit.TestContext.Current.CancellationToken);
        await _repo.SavePolygonAndAppendHistoryAsync(
            other, """{}""", 200.0, userId, "Saved", Clock.GetCurrentInstant(), Xunit.TestContext.Current.CancellationToken);

        var result = await _repo.GetPolygonsByCampSeasonIdsAsync([matching], Xunit.TestContext.Current.CancellationToken);

        result.Should().HaveCount(1);
        result[0].CampSeasonId.Should().Be(matching);
    }

    [HumansFact]
    public async Task GetPolygonsByCampSeasonIdsAsync_EmptyInput_ReturnsEmpty()
    {
        var result = await _repo.GetPolygonsByCampSeasonIdsAsync([], Xunit.TestContext.Current.CancellationToken);
        result.Should().BeEmpty();
    }

    [HumansFact]
    public async Task GetCampSeasonIdsWithPolygonAsync_ReturnsMatchingIdsOnly()
    {
        var withPolygon = Guid.NewGuid();
        var withoutPolygon = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await _repo.SavePolygonAndAppendHistoryAsync(
            withPolygon, """{}""", 100.0, userId, "Saved", Clock.GetCurrentInstant(), Xunit.TestContext.Current.CancellationToken);

        var result = await _repo.GetCampSeasonIdsWithPolygonAsync([withPolygon, withoutPolygon], Xunit.TestContext.Current.CancellationToken);

        result.Should().ContainSingle().Which.Should().Be(withPolygon);
    }

    [HumansFact(Timeout = 10000)]
    public async Task GetHistoryForCampSeasonAsync_ReturnsAllEntriesForCampSeason()
    {
        // Display ordering (newest first) moved to the controller
        // (CityPlanningApiController.GetCampPolygonHistory) per
        // memory/architecture/display-sort-in-controllers.md, so the repository
        // only guarantees membership, not order.
        var campSeasonId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await _repo.SavePolygonAndAppendHistoryAsync(
            campSeasonId, """{}""", 100.0, userId, "Saved", Clock.GetCurrentInstant(), Xunit.TestContext.Current.CancellationToken);
        Clock.Advance(Duration.FromSeconds(1));
        await _repo.SavePolygonAndAppendHistoryAsync(
            campSeasonId, """{}""", 200.0, userId, "Saved", Clock.GetCurrentInstant(), Xunit.TestContext.Current.CancellationToken);

        var result = await _repo.GetHistoryForCampSeasonAsync(campSeasonId, Xunit.TestContext.Current.CancellationToken);

        result.Should().HaveCount(2);
        result.Select(h => h.AreaSqm).Should().BeEquivalentTo([100.0, 200.0]);
    }

    [HumansFact]
    public async Task GetHistoryEntryAsync_ReturnsNull_WhenNotMatching()
    {
        var result = await _repo.GetHistoryEntryAsync(Guid.NewGuid(), Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);
        result.Should().BeNull();
    }

    [HumansFact]
    public async Task GetHistoryEntryAsync_DoesNotCrossCampSeasonId()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await _repo.SavePolygonAndAppendHistoryAsync(
            a, """{}""", 100.0, userId, "Saved", Clock.GetCurrentInstant(), Xunit.TestContext.Current.CancellationToken);
        var historyA = (await _repo.GetHistoryForCampSeasonAsync(a, Xunit.TestContext.Current.CancellationToken)).Single();

        // Wrong campSeasonId should not return the row.
        var result = await _repo.GetHistoryEntryAsync(b, historyA.Id, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeNull();
    }

    // ==========================================================================
    // DeletePolygonsForCampSeasonsAsync — the cross-section contract Camps calls
    // ==========================================================================

    [HumansFact]
    public async Task DeletePolygonsForCampSeasonsAsync_RemovesPolygonAndHistory_ForNamedSeasonsOnly()
    {
        var deleted = Guid.NewGuid();
        var kept = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await _repo.SavePolygonAndAppendHistoryAsync(
            deleted, """{}""", 100.0, userId, "Saved", Clock.GetCurrentInstant(), Xunit.TestContext.Current.CancellationToken);
        Clock.Advance(Duration.FromSeconds(1));
        await _repo.SavePolygonAndAppendHistoryAsync(
            deleted, """{}""", 150.0, userId, "Saved", Clock.GetCurrentInstant(), Xunit.TestContext.Current.CancellationToken);
        await _repo.SavePolygonAndAppendHistoryAsync(
            kept, """{}""", 200.0, userId, "Saved", Clock.GetCurrentInstant(), Xunit.TestContext.Current.CancellationToken);

        // One polygon plus its two history rows.
        var removed = await _repo.DeletePolygonsForCampSeasonsAsync([deleted], Xunit.TestContext.Current.CancellationToken);

        removed.Should().Be(3);
        (await _repo.GetPolygonsByCampSeasonIdsAsync([deleted], Xunit.TestContext.Current.CancellationToken)).Should().BeEmpty();
        (await _repo.GetHistoryForCampSeasonAsync(deleted, Xunit.TestContext.Current.CancellationToken)).Should().BeEmpty();
        (await _repo.GetPolygonsByCampSeasonIdsAsync([kept], Xunit.TestContext.Current.CancellationToken)).Should().ContainSingle();
        (await _repo.GetHistoryForCampSeasonAsync(kept, Xunit.TestContext.Current.CancellationToken)).Should().ContainSingle();
    }

    [HumansFact]
    public async Task DeletePolygonsForCampSeasonsAsync_EmptyInput_IsNoOp()
    {
        var campSeasonId = Guid.NewGuid();
        await _repo.SavePolygonAndAppendHistoryAsync(
            campSeasonId, """{}""", 100.0, Guid.NewGuid(), "Saved", Clock.GetCurrentInstant(), Xunit.TestContext.Current.CancellationToken);

        var removed = await _repo.DeletePolygonsForCampSeasonsAsync([], Xunit.TestContext.Current.CancellationToken);

        removed.Should().Be(0);
        (await _repo.GetPolygonsByCampSeasonIdsAsync([campSeasonId], Xunit.TestContext.Current.CancellationToken)).Should().ContainSingle();
    }

    [HumansFact]
    public async Task DeletePolygonsForCampSeasonsAsync_UnknownSeason_ReturnsZero()
    {
        var removed = await _repo.DeletePolygonsForCampSeasonsAsync([Guid.NewGuid()], Xunit.TestContext.Current.CancellationToken);
        removed.Should().Be(0);
    }

    // ==========================================================================
    // CityPlanningSettings operations
    // ==========================================================================

    [HumansFact]
    public async Task GetOrCreateSettingsAsync_CreatesRow_WhenNotFound()
    {
        var now = Clock.GetCurrentInstant();

        var result = await _repo.GetOrCreateSettingsAsync(2027, now, Xunit.TestContext.Current.CancellationToken);

        result.Year.Should().Be(2027);
        result.IsPlacementOpen.Should().BeFalse();
        result.UpdatedAt.Should().Be(now);
        (await CityPlanningDb.CityPlanningSettings.AsNoTracking().CountAsync(s => s.Year == 2027, Xunit.TestContext.Current.CancellationToken))
            .Should().Be(1);
    }

    [HumansFact]
    public async Task GetOrCreateSettingsAsync_IsIdempotent()
    {
        var now = Clock.GetCurrentInstant();
        await _repo.GetOrCreateSettingsAsync(2027, now, Xunit.TestContext.Current.CancellationToken);
        await _repo.GetOrCreateSettingsAsync(2027, now, Xunit.TestContext.Current.CancellationToken);

        (await CityPlanningDb.CityPlanningSettings.AsNoTracking().CountAsync(s => s.Year == 2027, Xunit.TestContext.Current.CancellationToken))
            .Should().Be(1);
    }

    [HumansFact]
    public async Task MutateSettingsAsync_CreatesRow_WhenMissing()
    {
        var now = Clock.GetCurrentInstant();

        await _repo.MutateSettingsAsync(2028, s => s.IsPlacementOpen = true, now, Xunit.TestContext.Current.CancellationToken);

        var result = await _repo.GetOrCreateSettingsAsync(2028, now, Xunit.TestContext.Current.CancellationToken);
        result.IsPlacementOpen.Should().BeTrue();
        result.UpdatedAt.Should().Be(now);
    }

    [HumansFact]
    public async Task MutateSettingsAsync_AppliesChange_AndSetsUpdatedAt()
    {
        CityPlanningDb.CityPlanningSettings.Add(new CityPlanningSettings
        {
            Year = 2026,
            IsPlacementOpen = false,
            UpdatedAt = Clock.GetCurrentInstant()
        });
        await CityPlanningDb.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        Clock.Advance(Duration.FromSeconds(10));
        var now = Clock.GetCurrentInstant();

        await _repo.MutateSettingsAsync(
            2026,
            s =>
            {
                s.IsPlacementOpen = true;
                s.OpenedAt = now;
            },
            now, Xunit.TestContext.Current.CancellationToken);

        var result = await _repo.GetOrCreateSettingsAsync(2026, now, Xunit.TestContext.Current.CancellationToken);
        result.IsPlacementOpen.Should().BeTrue();
        result.OpenedAt.Should().Be(now);
        result.UpdatedAt.Should().Be(now);
    }
}
