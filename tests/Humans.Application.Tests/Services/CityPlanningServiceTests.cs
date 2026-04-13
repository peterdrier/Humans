using AwesomeAssertions;
using Humans.Application.Interfaces;
using Xunit;
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
    private readonly ICampService _campService;
    private readonly ITeamService _teamService;
    private readonly IProfileService _profileService;
    private readonly CityPlanningService _sut;
    private readonly CityPlanningOptions _options = new() { CityPlanningTeamSlug = "city-planning" };

    public CityPlanningServiceTests()
    {
        var dbOptions = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new HumansDbContext(dbOptions);
        _clock = new FakeClock(Instant.FromUtc(2026, 3, 15, 12, 0, 0));
        _campService = Substitute.For<ICampService>();
        _teamService = Substitute.For<ITeamService>();
        _profileService = Substitute.For<IProfileService>();
        _sut = new CityPlanningService(_dbContext, _clock, Options.Create(_options),
            _campService, _teamService, _profileService);
    }

    public void Dispose() => _dbContext.Dispose();

    // --- Helpers ---

    private void SetupCampSettings(int publicYear = 2026)
    {
        _campService.GetSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(new CampSettings { Id = Guid.NewGuid(), PublicYear = publicYear });
    }

    private async Task<CityPlanningSettings> SeedMapSettingsAsync(int year = 2026, bool placementOpen = false)
    {
        SetupCampSettings(year);
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

    private static Guid NewUserId() => Guid.NewGuid();

    // --- Tests ---

    [Fact]
    public async Task SaveCampPolygonAsync_FirstSave_CreatesBothPolygonAndHistory()
    {
        var campSeasonId = Guid.NewGuid();
        var userId = NewUserId();
        const string geoJson = """{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[]]}}""";

        await _sut.SaveCampPolygonAsync(campSeasonId, geoJson, 500.0, userId);

        var polygon = await _dbContext.CampPolygons.SingleAsync(p => p.CampSeasonId == campSeasonId);
        var history = await _dbContext.CampPolygonHistories.SingleAsync(h => h.CampSeasonId == campSeasonId);

        polygon.GeoJson.Should().Be(geoJson);
        polygon.AreaSqm.Should().Be(500.0);
        history.Note.Should().Be("Saved");
        history.ModifiedAt.Should().Be(_clock.GetCurrentInstant());
    }

    [Fact]
    public async Task SaveCampPolygonAsync_SecondSave_UpdatesPolygonAndAppendsHistory()
    {
        var campSeasonId = Guid.NewGuid();
        var userId = NewUserId();
        const string geoJson1 = """{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,0]]]}}""";
        const string geoJson2 = """{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[[0,0],[2,0],[2,2],[0,0]]]}}""";

        await _sut.SaveCampPolygonAsync(campSeasonId, geoJson1, 100.0, userId);
        await _sut.SaveCampPolygonAsync(campSeasonId, geoJson2, 200.0, userId);

        var polygonCount = await _dbContext.CampPolygons.CountAsync(p => p.CampSeasonId == campSeasonId);
        var historyCount = await _dbContext.CampPolygonHistories.CountAsync(h => h.CampSeasonId == campSeasonId);
        var polygon = await _dbContext.CampPolygons.SingleAsync(p => p.CampSeasonId == campSeasonId);

        polygonCount.Should().Be(1);
        historyCount.Should().Be(2);
        polygon.GeoJson.Should().Be(geoJson2);
        polygon.AreaSqm.Should().Be(200.0);
    }

    [Fact]
    public async Task RestoreCampPolygonVersionAsync_RestoresGeoJsonWithNote()
    {
        var campSeasonId = Guid.NewGuid();
        var userId = NewUserId();
        const string originalGeoJson = """{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,0]]]}}""";

        var (_, historyEntry) = await _sut.SaveCampPolygonAsync(campSeasonId, originalGeoJson, 100.0, userId);
        _clock.Advance(Duration.FromSeconds(1));
        await _sut.SaveCampPolygonAsync(campSeasonId, """{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[[0,0],[5,0],[5,5],[0,0]]]}}""", 999.0, userId);
        _clock.Advance(Duration.FromSeconds(1));

        await _sut.RestoreCampPolygonVersionAsync(campSeasonId, historyEntry.Id, userId);

        var polygon = await _dbContext.CampPolygons.SingleAsync(p => p.CampSeasonId == campSeasonId);
        var latestHistory = await _dbContext.CampPolygonHistories
            .OrderByDescending(h => h.ModifiedAt).FirstAsync(h => h.CampSeasonId == campSeasonId);

        polygon.GeoJson.Should().Be(originalGeoJson);
        latestHistory.Note.Should().StartWith("Restored from");
    }

    [Fact]
    public async Task IsCityPlanningTeamMemberAsync_TeamMember_ReturnsTrue()
    {
        var userId = NewUserId();
        var teamId = Guid.NewGuid();
        var team = new Team { Id = teamId, Name = "City Planning", Slug = "city-planning" };

        _teamService.GetTeamBySlugAsync("city-planning", Arg.Any<CancellationToken>())
            .Returns(team);
        _teamService.IsUserMemberOfTeamAsync(teamId, userId, Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _sut.IsCityPlanningTeamMemberAsync(userId);
        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsCityPlanningTeamMemberAsync_NonMember_ReturnsFalse()
    {
        var userId = NewUserId();
        var teamId = Guid.NewGuid();
        var team = new Team { Id = teamId, Name = "City Planning", Slug = "city-planning" };

        _teamService.GetTeamBySlugAsync("city-planning", Arg.Any<CancellationToken>())
            .Returns(team);
        _teamService.IsUserMemberOfTeamAsync(teamId, userId, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _sut.IsCityPlanningTeamMemberAsync(userId);
        result.Should().BeFalse();
    }

    [Fact]
    public async Task CanUserEditAsync_CityPlanningTeamMember_AlwaysTrue_EvenWhenPlacementClosed()
    {
        var userId = NewUserId();
        var campSeasonId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var team = new Team { Id = teamId, Name = "City Planning", Slug = "city-planning" };
        await SeedMapSettingsAsync(placementOpen: false);

        _teamService.GetTeamBySlugAsync("city-planning", Arg.Any<CancellationToken>())
            .Returns(team);
        _teamService.IsUserMemberOfTeamAsync(teamId, userId, Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _sut.CanUserEditAsync(userId, campSeasonId);
        result.Should().BeTrue();
    }

    [Fact]
    public async Task CanUserEditAsync_LeadWithPlacementOpen_ReturnsTrue()
    {
        var userId = NewUserId();
        var campId = Guid.NewGuid();
        var campSeasonId = Guid.NewGuid();
        await SeedMapSettingsAsync(placementOpen: true);

        _teamService.GetTeamBySlugAsync("city-planning", Arg.Any<CancellationToken>())
            .Returns((Team?)null);

        _campService.GetCampSeasonInfoAsync(campSeasonId, Arg.Any<CancellationToken>())
            .Returns(new CampSeasonInfo(campSeasonId, campId, 2026));
        _campService.IsUserCampLeadAsync(userId, campId, Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _sut.CanUserEditAsync(userId, campSeasonId);
        result.Should().BeTrue();
    }

    [Fact]
    public async Task CanUserEditAsync_LeadWithPlacementClosed_ReturnsFalse()
    {
        var userId = NewUserId();
        var campId = Guid.NewGuid();
        var campSeasonId = Guid.NewGuid();
        await SeedMapSettingsAsync(placementOpen: false);

        _teamService.GetTeamBySlugAsync("city-planning", Arg.Any<CancellationToken>())
            .Returns((Team?)null);

        _campService.GetCampSeasonInfoAsync(campSeasonId, Arg.Any<CancellationToken>())
            .Returns(new CampSeasonInfo(campSeasonId, campId, 2026));
        _campService.IsUserCampLeadAsync(userId, campId, Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _sut.CanUserEditAsync(userId, campSeasonId);
        result.Should().BeFalse();
    }

    [Fact]
    public async Task CanUserEditAsync_LeadOfDifferentCamp_ReturnsFalse()
    {
        var userId = NewUserId();
        var campId = Guid.NewGuid();
        var campSeasonId = Guid.NewGuid();
        await SeedMapSettingsAsync(placementOpen: true);

        _teamService.GetTeamBySlugAsync("city-planning", Arg.Any<CancellationToken>())
            .Returns((Team?)null);

        _campService.GetCampSeasonInfoAsync(campSeasonId, Arg.Any<CancellationToken>())
            .Returns(new CampSeasonInfo(campSeasonId, campId, 2026));
        _campService.IsUserCampLeadAsync(userId, campId, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _sut.CanUserEditAsync(userId, campSeasonId);
        result.Should().BeFalse();
    }

    [Fact]
    public async Task CanUserEditAsync_LeadOfDifferentYear_ReturnsFalse()
    {
        var userId = NewUserId();
        var campId = Guid.NewGuid();
        var campSeasonId = Guid.NewGuid();
        await SeedMapSettingsAsync(year: 2026, placementOpen: true);

        _teamService.GetTeamBySlugAsync("city-planning", Arg.Any<CancellationToken>())
            .Returns((Team?)null);

        // Camp season is for 2027, but settings year is 2026
        _campService.GetCampSeasonInfoAsync(campSeasonId, Arg.Any<CancellationToken>())
            .Returns(new CampSeasonInfo(campSeasonId, campId, 2027));

        var result = await _sut.CanUserEditAsync(userId, campSeasonId);
        result.Should().BeFalse();
    }

    [Fact]
    public async Task GetSettingsAsync_CreatesRowIfMissing()
    {
        SetupCampSettings(publicYear: 2026);

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
        var adminId = NewUserId();

        await _sut.OpenPlacementAsync(adminId);

        var settings = await _dbContext.CityPlanningSettings.SingleAsync();
        settings.IsPlacementOpen.Should().BeTrue();
        settings.OpenedAt.Should().Be(_clock.GetCurrentInstant());
    }

    [Fact]
    public async Task ClosePlacementAsync_SetsIsPlacementOpenFalse()
    {
        await SeedMapSettingsAsync(placementOpen: true);
        var adminId = NewUserId();

        await _sut.ClosePlacementAsync(adminId);

        var settings = await _dbContext.CityPlanningSettings.SingleAsync();
        settings.IsPlacementOpen.Should().BeFalse();
        settings.ClosedAt.Should().Be(_clock.GetCurrentInstant());
    }

    [Fact]
    public async Task GetCampPolygonsAsync_ReturnsOnlyPolygonsForYear()
    {
        var season2026Id = Guid.NewGuid();
        var season2027Id = Guid.NewGuid();
        var userId = NewUserId();

        await _sut.SaveCampPolygonAsync(season2026Id, """{"type":"Feature"}""", 100, userId);
        await _sut.SaveCampPolygonAsync(season2027Id, """{"type":"Feature"}""", 200, userId);

        _campService.GetCampSeasonDisplayDataForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, CampSeasonDisplayData>
            {
                [season2026Id] = new("Test Camp 2026", "test-camp", null)
            });

        var result = await _sut.GetCampPolygonsAsync(2026);

        result.Should().HaveCount(1);
        result[0].CampSeasonId.Should().Be(season2026Id);
    }

    [Fact]
    public async Task GetCampSeasonsWithoutCampPolygonAsync_ExcludesSeasonsWithPolygon()
    {
        var seasonWithId = Guid.NewGuid();
        var seasonWithoutId = Guid.NewGuid();
        var userId = NewUserId();

        await _sut.SaveCampPolygonAsync(seasonWithId, """{"type":"Feature"}""", 100, userId);

        _campService.GetCampSeasonBriefsForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns(new List<CampSeasonBrief>
            {
                new(seasonWithId, "Camp With", "camp-with"),
                new(seasonWithoutId, "Camp Without", "camp-without")
            });

        var result = await _sut.GetCampSeasonsWithoutCampPolygonAsync(2026);

        result.Should().HaveCount(1);
        result[0].CampSeasonId.Should().Be(seasonWithoutId);
    }

    [Fact]
    public async Task ExportAsGeoJsonAsync_ReturnsFeatureCollection()
    {
        var campSeasonId = Guid.NewGuid();
        var userId = NewUserId();
        const string geoJson = """{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,0]]]},"properties":{}}""";

        await _sut.SaveCampPolygonAsync(campSeasonId, geoJson, 100.0, userId);

        _campService.GetCampSeasonDisplayDataForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, CampSeasonDisplayData>
            {
                [campSeasonId] = new("Test Camp", "test-camp", null)
            });

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
        var campSeasonId = Guid.NewGuid();
        var userId = NewUserId();
        // Seed a user for the nav property (still needed for ModifiedByUser Include)
        var user = new User { Id = userId, UserName = "test@test.com", Email = "test@test.com", DisplayName = "Test User" };
        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync();

        _clock.Advance(Duration.FromSeconds(1));
        await _sut.SaveCampPolygonAsync(campSeasonId, """{"type":"Feature"}""", 100.0, userId);
        _clock.Advance(Duration.FromSeconds(1));
        await _sut.SaveCampPolygonAsync(campSeasonId, """{"type":"Feature"}""", 200.0, userId);

        var history = await _sut.GetCampPolygonHistoryAsync(campSeasonId);

        history.Should().HaveCount(2);
        history[0].AreaSqm.Should().Be(200.0); // Most recent first
        history[1].AreaSqm.Should().Be(100.0);
    }

    [Fact]
    public async Task GetCampPolygonsAsync_IncludesSoundZone_WhenSet()
    {
        var campSeasonId = Guid.NewGuid();
        var userId = NewUserId();
        const string geoJson = """{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[]]}}""";
        await _sut.SaveCampPolygonAsync(campSeasonId, geoJson, 100.0, userId);

        _campService.GetCampSeasonDisplayDataForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, CampSeasonDisplayData>
            {
                [campSeasonId] = new("Test Camp", "test-camp", SoundZone.Blue)
            });

        var polygons = await _sut.GetCampPolygonsAsync(2026);

        polygons.Single().SoundZone.Should().Be(SoundZone.Blue);
    }

    [Fact]
    public async Task GetCampPolygonsAsync_SoundZoneIsNull_WhenNotSet()
    {
        var campSeasonId = Guid.NewGuid();
        var userId = NewUserId();
        const string geoJson = """{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[]]}}""";
        await _sut.SaveCampPolygonAsync(campSeasonId, geoJson, 100.0, userId);

        _campService.GetCampSeasonDisplayDataForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, CampSeasonDisplayData>
            {
                [campSeasonId] = new("Test Camp", "test-camp", null)
            });

        var polygons = await _sut.GetCampPolygonsAsync(2026);

        polygons.Single().SoundZone.Should().BeNull();
    }

    [Fact]
    public async Task GetCampSeasonSoundZoneAsync_ReturnsSoundZone_WhenSet()
    {
        var campSeasonId = Guid.NewGuid();
        _campService.GetCampSeasonSoundZoneAsync(campSeasonId, Arg.Any<CancellationToken>())
            .Returns(SoundZone.Red);

        var result = await _sut.GetCampSeasonSoundZoneAsync(campSeasonId);

        result.Should().Be(SoundZone.Red);
    }

    [Fact]
    public async Task GetCampSeasonSoundZoneAsync_ReturnsNull_WhenNotSet()
    {
        var campSeasonId = Guid.NewGuid();
        _campService.GetCampSeasonSoundZoneAsync(campSeasonId, Arg.Any<CancellationToken>())
            .Returns((SoundZone?)null);

        var result = await _sut.GetCampSeasonSoundZoneAsync(campSeasonId);

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

    [Fact]
    public async Task GetUserDisplayNameAsync_ReturnsProfileBurnerName()
    {
        var userId = Guid.NewGuid();
        _profileService.GetProfileAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new Profile { UserId = userId, BurnerName = "Burner Name" });

        var result = await _sut.GetUserDisplayNameAsync(userId);

        result.Should().Be("Burner Name");
    }

    [Fact]
    public async Task GetUserDisplayNameAsync_ReturnsNull_WhenNoProfile()
    {
        var userId = Guid.NewGuid();
        _profileService.GetProfileAsync(userId, Arg.Any<CancellationToken>())
            .Returns((Profile?)null);

        var result = await _sut.GetUserDisplayNameAsync(userId);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetUserCampSeasonIdForYearAsync_DelegatesToCampService()
    {
        var userId = Guid.NewGuid();
        var campSeasonId = Guid.NewGuid();
        _campService.GetCampLeadSeasonIdForYearAsync(userId, 2026, Arg.Any<CancellationToken>())
            .Returns(campSeasonId);

        var result = await _sut.GetUserCampSeasonIdForYearAsync(userId, 2026);

        result.Should().Be(campSeasonId);
    }
}
