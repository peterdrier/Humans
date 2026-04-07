using AwesomeAssertions;
using Humans.Application.Interfaces;
using Xunit;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Application.Tests.Services;

public class CityPlanningServiceTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly CityPlanningService _sut;
    private readonly CityPlanningOptions _options = new() { CityPlanningTeamSlug = "city-planning" };

    public CityPlanningServiceTests()
    {
        var dbOptions = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new HumansDbContext(dbOptions);
        _clock = new FakeClock(Instant.FromUtc(2026, 3, 15, 12, 0, 0));
        _sut = new CityPlanningService(_dbContext, _clock, Options.Create(_options));
    }

    public void Dispose() => _dbContext.Dispose();

    // --- Helpers ---

    private async Task<(Camp camp, CampSeason season, User user)> SeedCampWithLeadAsync(int year = 2026)
    {
        var user = new User { Id = Guid.NewGuid(), UserName = "lead@test.com", Email = "lead@test.com" };
        _dbContext.Users.Add(user);

        var camp = new Camp { Id = Guid.NewGuid(), Slug = "test-camp", ContactEmail = "e@test.com", CreatedByUserId = user.Id };
        _dbContext.Camps.Add(camp);

        var season = new CampSeason { Id = Guid.NewGuid(), CampId = camp.Id, Year = year, Name = "Test Camp", Status = CampSeasonStatus.Active };
        _dbContext.CampSeasons.Add(season);

        _dbContext.CampLeads.Add(new CampLead { Id = Guid.NewGuid(), CampId = camp.Id, UserId = user.Id, Role = CampLeadRole.Primary });

        await _dbContext.SaveChangesAsync();
        return (camp, season, user);
    }

    private async Task SeedCampSettingsAsync(int publicYear = 2026)
    {
        _dbContext.CampSettings.Add(new CampSettings { Id = Guid.NewGuid(), PublicYear = publicYear });
        await _dbContext.SaveChangesAsync();
    }

    private async Task<CityPlanningSettings> SeedMapSettingsAsync(int year = 2026, bool placementOpen = false)
    {
        await SeedCampSettingsAsync(year);
        var settings = new CityPlanningSettings
        {
            Year = year,
            IsPlacementOpen = placementOpen,
            UpdatedAt = _clock.GetCurrentInstant()
        };
        _dbContext.CityPlanningSettings.Add(settings);
        await _dbContext.SaveChangesAsync();
        return settings;
    }

    private async Task<Guid> SeedCampAdminUserAsync()
    {
        var user = new User { Id = Guid.NewGuid(), UserName = "admin@test.com", Email = "admin@test.com" };
        _dbContext.Users.Add(user);

        _dbContext.RoleAssignments.Add(new RoleAssignment
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            RoleName = RoleNames.CampAdmin,
            ValidFrom = _clock.GetCurrentInstant().Minus(Duration.FromHours(1)),
            CreatedAt = _clock.GetCurrentInstant(),
            CreatedByUserId = user.Id
        });

        await _dbContext.SaveChangesAsync();
        return user.Id;
    }

    private async Task<Guid> SeedCityPlanningTeamMemberAsync()
    {
        var user = new User { Id = Guid.NewGuid(), UserName = "planner@test.com", Email = "planner@test.com" };
        _dbContext.Users.Add(user);

        var team = new Team { Id = Guid.NewGuid(), Name = "City Planning", Slug = "city-planning" };
        _dbContext.Teams.Add(team);
        _dbContext.TeamMembers.Add(new TeamMember { Id = Guid.NewGuid(), TeamId = team.Id, UserId = user.Id });

        await _dbContext.SaveChangesAsync();
        return user.Id;
    }

    // --- Tests ---

    [Fact]
    public async Task SaveCampPolygonAsync_FirstSave_CreatesBothPolygonAndHistory()
    {
        var (_, season, user) = await SeedCampWithLeadAsync();
        const string geoJson = """{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[]]}}""";

        await _sut.SaveCampPolygonAsync(season.Id, geoJson, 500.0, user.Id);

        var polygon = await _dbContext.CampPolygons.SingleAsync(p => p.CampSeasonId == season.Id);
        var history = await _dbContext.CampPolygonHistories.SingleAsync(h => h.CampSeasonId == season.Id);

        polygon.GeoJson.Should().Be(geoJson);
        polygon.AreaSqm.Should().Be(500.0);
        history.Note.Should().Be("Saved");
        history.ModifiedAt.Should().Be(_clock.GetCurrentInstant());
    }

    [Fact]
    public async Task SaveCampPolygonAsync_SecondSave_UpdatesPolygonAndAppendsHistory()
    {
        var (_, season, user) = await SeedCampWithLeadAsync();
        const string geoJson1 = """{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,0]]]}}""";
        const string geoJson2 = """{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[[0,0],[2,0],[2,2],[0,0]]]}}""";

        await _sut.SaveCampPolygonAsync(season.Id, geoJson1, 100.0, user.Id);
        await _sut.SaveCampPolygonAsync(season.Id, geoJson2, 200.0, user.Id);

        var polygonCount = await _dbContext.CampPolygons.CountAsync(p => p.CampSeasonId == season.Id);
        var historyCount = await _dbContext.CampPolygonHistories.CountAsync(h => h.CampSeasonId == season.Id);
        var polygon = await _dbContext.CampPolygons.SingleAsync(p => p.CampSeasonId == season.Id);

        polygonCount.Should().Be(1);
        historyCount.Should().Be(2);
        polygon.GeoJson.Should().Be(geoJson2);
        polygon.AreaSqm.Should().Be(200.0);
    }

    [Fact]
    public async Task RestoreCampPolygonVersionAsync_RestoresGeoJsonWithNote()
    {
        var (_, season, user) = await SeedCampWithLeadAsync();
        const string originalGeoJson = """{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,0]]]}}""";

        var (_, historyEntry) = await _sut.SaveCampPolygonAsync(season.Id, originalGeoJson, 100.0, user.Id);
        _clock.Advance(Duration.FromSeconds(1));
        await _sut.SaveCampPolygonAsync(season.Id, """{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[[0,0],[5,0],[5,5],[0,0]]]}}""", 999.0, user.Id);
        _clock.Advance(Duration.FromSeconds(1));

        await _sut.RestoreCampPolygonVersionAsync(season.Id, historyEntry.Id, user.Id);

        var polygon = await _dbContext.CampPolygons.SingleAsync(p => p.CampSeasonId == season.Id);
        var latestHistory = await _dbContext.CampPolygonHistories
            .OrderByDescending(h => h.ModifiedAt).FirstAsync(h => h.CampSeasonId == season.Id);

        polygon.GeoJson.Should().Be(originalGeoJson);
        latestHistory.Note.Should().StartWith("Restored from");
    }

    [Fact]
    public async Task IsCityPlanningTeamMemberAsync_TeamMember_ReturnsTrue()
    {
        var plannerId = await SeedCityPlanningTeamMemberAsync();
        var result = await _sut.IsCityPlanningTeamMemberAsync(plannerId);
        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsCityPlanningTeamMemberAsync_NonMember_ReturnsFalse()
    {
        var otherUser = new User { Id = Guid.NewGuid(), UserName = "other@test.com", Email = "other@test.com" };
        _dbContext.Users.Add(otherUser);
        await _dbContext.SaveChangesAsync();

        var result = await _sut.IsCityPlanningTeamMemberAsync(otherUser.Id);
        result.Should().BeFalse();
    }

    [Fact]
    public async Task CanUserEditAsync_CityPlanningTeamMember_AlwaysTrue_EvenWhenPlacementClosed()
    {
        var plannerId = await SeedCityPlanningTeamMemberAsync();
        var (_, season, _) = await SeedCampWithLeadAsync();
        await SeedMapSettingsAsync(placementOpen: false);

        var result = await _sut.CanUserEditAsync(plannerId, season.Id);
        result.Should().BeTrue();
    }

    [Fact]
    public async Task CanUserEditAsync_LeadWithPlacementOpen_ReturnsTrue()
    {
        var (_, season, user) = await SeedCampWithLeadAsync();
        await SeedMapSettingsAsync(placementOpen: true);

        var result = await _sut.CanUserEditAsync(user.Id, season.Id);
        result.Should().BeTrue();
    }

    [Fact]
    public async Task CanUserEditAsync_LeadWithPlacementClosed_ReturnsFalse()
    {
        var (_, season, user) = await SeedCampWithLeadAsync();
        await SeedMapSettingsAsync(placementOpen: false);

        var result = await _sut.CanUserEditAsync(user.Id, season.Id);
        result.Should().BeFalse();
    }

    [Fact]
    public async Task CanUserEditAsync_LeadOfDifferentCamp_ReturnsFalse()
    {
        var (_, season, _) = await SeedCampWithLeadAsync();
        await SeedMapSettingsAsync(placementOpen: true);

        // A different user who is NOT a lead on this camp
        var otherUser = new User { Id = Guid.NewGuid(), UserName = "other@test.com", Email = "other@test.com" };
        _dbContext.Users.Add(otherUser);
        await _dbContext.SaveChangesAsync();

        var result = await _sut.CanUserEditAsync(otherUser.Id, season.Id);
        result.Should().BeFalse();
    }

    [Fact]
    public async Task CanUserEditAsync_LeadOfDifferentYear_ReturnsFalse()
    {
        var (_, season2027, user) = await SeedCampWithLeadAsync(year: 2027);
        await SeedMapSettingsAsync(year: 2026, placementOpen: true);

        var result = await _sut.CanUserEditAsync(user.Id, season2027.Id);
        result.Should().BeFalse();
    }

    [Fact]
    public async Task GetSettingsAsync_CreatesRowIfMissing()
    {
        await SeedCampSettingsAsync(publicYear: 2026);

        var settings = await _sut.GetSettingsAsync();

        settings.Year.Should().Be(2026);
        settings.IsPlacementOpen.Should().BeFalse();
        (await _dbContext.CityPlanningSettings.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task GetSettingsAsync_ReturnsExistingRow()
    {
        var existing = await SeedMapSettingsAsync(year: 2026, placementOpen: true);

        var result = await _sut.GetSettingsAsync();

        result.Id.Should().Be(existing.Id);
        result.IsPlacementOpen.Should().BeTrue();
        (await _dbContext.CityPlanningSettings.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task OpenPlacementAsync_SetsIsPlacementOpenTrue()
    {
        await SeedMapSettingsAsync(placementOpen: false);
        var adminId = await SeedCampAdminUserAsync();

        await _sut.OpenPlacementAsync(adminId);

        var settings = await _dbContext.CityPlanningSettings.SingleAsync();
        settings.IsPlacementOpen.Should().BeTrue();
        settings.OpenedAt.Should().Be(_clock.GetCurrentInstant());
    }

    [Fact]
    public async Task ClosePlacementAsync_SetsIsPlacementOpenFalse()
    {
        await SeedMapSettingsAsync(placementOpen: true);
        var adminId = await SeedCampAdminUserAsync();

        await _sut.ClosePlacementAsync(adminId);

        var settings = await _dbContext.CityPlanningSettings.SingleAsync();
        settings.IsPlacementOpen.Should().BeFalse();
        settings.ClosedAt.Should().Be(_clock.GetCurrentInstant());
    }

    [Fact]
    public async Task GetCampPolygonsAsync_ReturnsOnlyPolygonsForYear()
    {
        var (_, season2026, user) = await SeedCampWithLeadAsync(year: 2026);
        var (_, season2027, _) = await SeedCampWithLeadAsync(year: 2027);

        await _sut.SaveCampPolygonAsync(season2026.Id, """{"type":"Feature"}""", 100, user.Id);
        await _sut.SaveCampPolygonAsync(season2027.Id, """{"type":"Feature"}""", 200, user.Id);

        var result = await _sut.GetCampPolygonsAsync(2026);

        result.Should().HaveCount(1);
        result[0].CampSeasonId.Should().Be(season2026.Id);
    }

    [Fact]
    public async Task GetCampSeasonsWithoutCampPolygonAsync_ExcludesSeasonsWithPolygon()
    {
        var (_, seasonWith, user) = await SeedCampWithLeadAsync(year: 2026);
        var (_, seasonWithout, _) = await SeedCampWithLeadAsync(year: 2026);

        await _sut.SaveCampPolygonAsync(seasonWith.Id, """{"type":"Feature"}""", 100, user.Id);

        var result = await _sut.GetCampSeasonsWithoutCampPolygonAsync(2026);

        result.Should().HaveCount(1);
        result[0].CampSeasonId.Should().Be(seasonWithout.Id);
    }

    [Fact]
    public async Task ExportAsGeoJsonAsync_ReturnsFeatureCollection()
    {
        var (_, season, user) = await SeedCampWithLeadAsync(year: 2026);
        const string geoJson = """{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,0]]]},"properties":{}}""";

        await _sut.SaveCampPolygonAsync(season.Id, geoJson, 100.0, user.Id);

        var result = await _sut.ExportAsGeoJsonAsync(2026);

        using var doc = System.Text.Json.JsonDocument.Parse(result);
        doc.RootElement.GetProperty("type").GetString().Should().Be("FeatureCollection");
        var features = doc.RootElement.GetProperty("features");
        features.GetArrayLength().Should().Be(1);
        features[0].GetProperty("properties").GetProperty("areaSqm").GetDouble().Should().Be(100.0);
    }

    [Fact]
    public async Task GetCampPolygonHistoryAsync_ReturnsEntriesInDescendingOrder()
    {
        var (_, season, user) = await SeedCampWithLeadAsync();

        _clock.Advance(Duration.FromSeconds(1));
        await _sut.SaveCampPolygonAsync(season.Id, """{"type":"Feature"}""", 100.0, user.Id);
        _clock.Advance(Duration.FromSeconds(1));
        await _sut.SaveCampPolygonAsync(season.Id, """{"type":"Feature"}""", 200.0, user.Id);

        var history = await _sut.GetCampPolygonHistoryAsync(season.Id);

        history.Should().HaveCount(2);
        history[0].AreaSqm.Should().Be(200.0); // Most recent first
        history[1].AreaSqm.Should().Be(100.0);
    }

    [Fact]
    public async Task GetCampPolygonsAsync_IncludesSoundZone_WhenSet()
    {
        var (_, season, user) = await SeedCampWithLeadAsync();
        season.SoundZone = SoundZone.Blue;
        await _dbContext.SaveChangesAsync();
        const string geoJson = """{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[]]}}""";
        await _sut.SaveCampPolygonAsync(season.Id, geoJson, 100.0, user.Id);

        var polygons = await _sut.GetCampPolygonsAsync(season.Year);

        polygons.Single().SoundZone.Should().Be(SoundZone.Blue);
    }

    [Fact]
    public async Task GetCampPolygonsAsync_SoundZoneIsNull_WhenNotSet()
    {
        var (_, season, user) = await SeedCampWithLeadAsync();
        // SoundZone not set, defaults to null
        const string geoJson = """{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[]]}}""";
        await _sut.SaveCampPolygonAsync(season.Id, geoJson, 100.0, user.Id);

        var polygons = await _sut.GetCampPolygonsAsync(season.Year);

        polygons.Single().SoundZone.Should().BeNull();
    }

    [Fact]
    public async Task GetCampSeasonSoundZoneAsync_ReturnsSoundZone_WhenSet()
    {
        var (_, season, _) = await SeedCampWithLeadAsync();
        season.SoundZone = SoundZone.Red;
        await _dbContext.SaveChangesAsync();

        var result = await _sut.GetCampSeasonSoundZoneAsync(season.Id);

        result.Should().Be(SoundZone.Red);
    }

    [Fact]
    public async Task GetCampSeasonSoundZoneAsync_ReturnsNull_WhenNotSet()
    {
        var (_, season, _) = await SeedCampWithLeadAsync();

        var result = await _sut.GetCampSeasonSoundZoneAsync(season.Id);

        result.Should().BeNull();
    }

    // --- UpdatePlacementDatesAsync ---

    [Fact]
    public async Task UpdatePlacementDatesAsync_SetsBothDates()
    {
        await SeedMapSettingsAsync();
        var opens = new LocalDateTime(2026, 4, 10, 18, 0);
        var closes = new LocalDateTime(2026, 4, 20, 23, 59);

        await _sut.UpdatePlacementDatesAsync(opens, closes);

        var settings = await _dbContext.CityPlanningSettings.SingleAsync();
        settings.PlacementOpensAt.Should().Be(opens);
        settings.PlacementClosesAt.Should().Be(closes);
    }

    [Fact]
    public async Task UpdatePlacementDatesAsync_ClearsDates_WhenNull()
    {
        await SeedMapSettingsAsync();
        var settings = await _dbContext.CityPlanningSettings.SingleAsync();
        settings.PlacementOpensAt = new LocalDateTime(2026, 4, 10, 18, 0);
        settings.PlacementClosesAt = new LocalDateTime(2026, 4, 20, 23, 59);
        await _dbContext.SaveChangesAsync();

        await _sut.UpdatePlacementDatesAsync(null, null);

        var updated = await _dbContext.CityPlanningSettings.SingleAsync();
        updated.PlacementOpensAt.Should().BeNull();
        updated.PlacementClosesAt.Should().BeNull();
    }

    // --- UpdateOfficialZonesAsync / DeleteOfficialZonesAsync ---

    [Fact]
    public async Task UpdateOfficialZonesAsync_StoresGeoJson()
    {
        await SeedMapSettingsAsync();
        const string geoJson = """{"type":"FeatureCollection","features":[]}""";

        await _sut.UpdateOfficialZonesAsync(geoJson, Guid.NewGuid());

        var settings = await _dbContext.CityPlanningSettings.SingleAsync();
        settings.OfficialZonesGeoJson.Should().Be(geoJson);
        settings.UpdatedAt.Should().Be(_clock.GetCurrentInstant());
    }

    [Fact]
    public async Task DeleteOfficialZonesAsync_SetsNull()
    {
        await SeedMapSettingsAsync();
        var settings = await _dbContext.CityPlanningSettings.SingleAsync();
        settings.OfficialZonesGeoJson = """{"type":"FeatureCollection","features":[]}""";
        await _dbContext.SaveChangesAsync();

        await _sut.DeleteOfficialZonesAsync(Guid.NewGuid());

        var updated = await _dbContext.CityPlanningSettings.SingleAsync();
        updated.OfficialZonesGeoJson.Should().BeNull();
        updated.UpdatedAt.Should().Be(_clock.GetCurrentInstant());
    }
}
