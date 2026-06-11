using AwesomeAssertions;
using Humans.Application.Configuration;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.CityPlanning;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Repositories.CityPlanning;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using NSubstitute;

namespace Humans.Application.Tests.Services;

public sealed class CityPlanningServiceTests : ServiceTestHarness
{
    private readonly ICampServiceRead _campService;
    private readonly ITeamService _teamService;
    private readonly IUserService _userService;
    private readonly CityPlanningService _sut;
    private readonly CityPlanningOptions _options = new() { CityPlanningTeamSlug = "city-planning" };

    public CityPlanningServiceTests()
        : base(Instant.FromUtc(2026, 3, 15, 12, 0, 0))
    {
        _campService = Substitute.For<ICampServiceRead>();
        _teamService = Substitute.For<ITeamService>();
        _userService = Substitute.For<IUserService>();
        var repo = new CityPlanningRepository(DbFactory);
        _sut = new CityPlanningService(
            repo, Clock, Options.Create(_options),
            _campService, _teamService, _userService);
    }

    // --- Helpers ---

    private void SetupCampSettings(int publicYear = 2026)
    {
        _campService.GetSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(new CampSettingsInfo(publicYear, [], null));
    }

    private async Task<CityPlanningSettings> SeedMapSettingsAsync(int year = 2026, bool placementOpen = false)
    {
        SetupCampSettings(year);
        var settings = new CityPlanningSettings
        {
            Year = year,
            IsPlacementOpen = placementOpen,
            UpdatedAt = Clock.GetCurrentInstant()
        };
        Db.CityPlanningSettings.Add(settings);
        await Db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        return settings;
    }

    private static Guid NewUserId() => Guid.NewGuid();

    private static IFormFile CreateUpload(string content)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "upload.geojson");
    }

    // --- Tests ---

    [HumansFact]
    public async Task SaveCampPolygonAsync_FirstSave_CreatesBothPolygonAndHistory()
    {
        var campSeasonId = Guid.NewGuid();
        var userId = NewUserId();
        const string geoJson = """{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[]]}}""";

        await _sut.SaveCampPolygonAsync(campSeasonId, geoJson, 500.0, userId, cancellationToken: Xunit.TestContext.Current.CancellationToken);

        var polygon = await Db.CampPolygons.AsNoTracking().SingleAsync(p => p.CampSeasonId == campSeasonId, Xunit.TestContext.Current.CancellationToken);
        var history = await Db.CampPolygonHistories.AsNoTracking().SingleAsync(h => h.CampSeasonId == campSeasonId, Xunit.TestContext.Current.CancellationToken);

        polygon.GeoJson.Should().Be(geoJson);
        polygon.AreaSqm.Should().Be(500.0);
        history.Note.Should().Be("Saved");
        history.ModifiedAt.Should().Be(Clock.GetCurrentInstant());
    }

    [HumansFact]
    public async Task SaveCampPolygonAsync_SecondSave_UpdatesPolygonAndAppendsHistory()
    {
        var campSeasonId = Guid.NewGuid();
        var userId = NewUserId();
        const string geoJson1 = """{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,0]]]}}""";
        const string geoJson2 = """{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[[0,0],[2,0],[2,2],[0,0]]]}}""";

        await _sut.SaveCampPolygonAsync(campSeasonId, geoJson1, 100.0, userId, cancellationToken: Xunit.TestContext.Current.CancellationToken);
        await _sut.SaveCampPolygonAsync(campSeasonId, geoJson2, 200.0, userId, cancellationToken: Xunit.TestContext.Current.CancellationToken);

        var polygonCount = await Db.CampPolygons.AsNoTracking().CountAsync(p => p.CampSeasonId == campSeasonId, Xunit.TestContext.Current.CancellationToken);
        var historyCount = await Db.CampPolygonHistories.AsNoTracking().CountAsync(h => h.CampSeasonId == campSeasonId, Xunit.TestContext.Current.CancellationToken);
        var polygon = await Db.CampPolygons.AsNoTracking().SingleAsync(p => p.CampSeasonId == campSeasonId, Xunit.TestContext.Current.CancellationToken);

        polygonCount.Should().Be(1);
        historyCount.Should().Be(2);
        polygon.GeoJson.Should().Be(geoJson2);
        polygon.AreaSqm.Should().Be(200.0);
    }

    [HumansFact]
    public async Task SaveCampPolygonAsync_InvalidGeoJson_Throws()
    {
        var act = async () => await _sut.SaveCampPolygonAsync(
            Guid.NewGuid(), "{not-json", 100.0, NewUserId(), cancellationToken: Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("Invalid GeoJSON.*");
    }

    [HumansFact]
    public async Task UpdatePlacementDatesAsync_StringInputs_ParsesAndPersists()
    {
        await SeedMapSettingsAsync();

        var result = await _sut.UpdatePlacementDatesAsync("2026-06-01T08:30", "2026-06-02T18:45", Xunit.TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        var settings = await Db.CityPlanningSettings.AsNoTracking().SingleAsync(Xunit.TestContext.Current.CancellationToken);
        settings.PlacementOpensAt.Should().Be(new LocalDateTime(2026, 6, 1, 8, 30));
        settings.PlacementClosesAt.Should().Be(new LocalDateTime(2026, 6, 2, 18, 45));
    }

    [HumansFact]
    public async Task UpdatePlacementDatesAsync_InvalidOpenString_ReturnsError()
    {
        var result = await _sut.UpdatePlacementDatesAsync("not-a-date", null, Xunit.TestContext.Current.CancellationToken);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("InvalidOpensAt");
    }

    [HumansFact]
    public async Task UpdateLimitZoneFromUploadAsync_ValidFile_PersistsLimitZone()
    {
        await SeedMapSettingsAsync();
        var file = CreateUpload("""{"type":"FeatureCollection","features":[]}""");

        var result = await _sut.UpdateLimitZoneFromUploadAsync(file, NewUserId(), Xunit.TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        var settings = await Db.CityPlanningSettings.AsNoTracking().SingleAsync(Xunit.TestContext.Current.CancellationToken);
        settings.LimitZoneGeoJson.Should().Be("""{"type":"FeatureCollection","features":[]}""");
    }

    [HumansFact]
    public async Task UpdateLimitZoneFromUploadAsync_InvalidJson_ReturnsError()
    {
        var file = CreateUpload("{not-json");

        var result = await _sut.UpdateLimitZoneFromUploadAsync(file, NewUserId(), Xunit.TestContext.Current.CancellationToken);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("InvalidGeoJson");
    }

    [HumansFact]
    public async Task UpdateOfficialZonesFromUploadAsync_ValidFile_PersistsOfficialZones()
    {
        await SeedMapSettingsAsync();
        var file = CreateUpload("""{"type":"FeatureCollection","features":[]}""");

        var result = await _sut.UpdateOfficialZonesFromUploadAsync(file, NewUserId(), Xunit.TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        var settings = await Db.CityPlanningSettings.AsNoTracking().SingleAsync(Xunit.TestContext.Current.CancellationToken);
        settings.OfficialZonesGeoJson.Should().Be("""{"type":"FeatureCollection","features":[]}""");
    }

    [HumansFact]
    public async Task RestoreCampPolygonVersionAsync_RestoresGeoJsonWithNote()
    {
        var campSeasonId = Guid.NewGuid();
        var userId = NewUserId();
        const string originalGeoJson = """{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,0]]]}}""";

        await _sut.SaveCampPolygonAsync(campSeasonId, originalGeoJson, 100.0, userId, cancellationToken: Xunit.TestContext.Current.CancellationToken);
        var originalHistory = await Db.CampPolygonHistories.AsNoTracking()
            .SingleAsync(h => h.CampSeasonId == campSeasonId, Xunit.TestContext.Current.CancellationToken);
        Clock.Advance(Duration.FromSeconds(1));
        await _sut.SaveCampPolygonAsync(campSeasonId, """{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[[0,0],[5,0],[5,5],[0,0]]]}}""", 999.0, userId, cancellationToken: Xunit.TestContext.Current.CancellationToken);
        Clock.Advance(Duration.FromSeconds(1));

        await _sut.RestoreCampPolygonVersionAsync(campSeasonId, originalHistory.Id, userId, Xunit.TestContext.Current.CancellationToken);

        var polygon = await Db.CampPolygons.AsNoTracking().SingleAsync(p => p.CampSeasonId == campSeasonId, Xunit.TestContext.Current.CancellationToken);
        var latestHistory = await Db.CampPolygonHistories.AsNoTracking()
            .OrderByDescending(h => h.ModifiedAt).FirstAsync(h => h.CampSeasonId == campSeasonId, Xunit.TestContext.Current.CancellationToken);

        polygon.GeoJson.Should().Be(originalGeoJson);
        latestHistory.Note.Should().StartWith("Restored from");
    }

    [HumansFact]
    public async Task IsCityPlanningTeamMemberAsync_TeamMember_ReturnsTrue()
    {
        var userId = NewUserId();
        var teamId = Guid.NewGuid();
        SetupCityPlanningTeam(teamId, memberUserId: userId);

        var result = await _sut.IsCityPlanningTeamMemberAsync(userId, Xunit.TestContext.Current.CancellationToken);
        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task IsCityPlanningTeamMemberAsync_NonMember_ReturnsFalse()
    {
        var userId = NewUserId();
        var teamId = Guid.NewGuid();
        SetupCityPlanningTeam(teamId, memberUserId: null);

        var result = await _sut.IsCityPlanningTeamMemberAsync(userId, Xunit.TestContext.Current.CancellationToken);
        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task CanUserEditAsync_CityPlanningTeamMember_AlwaysTrue_EvenWhenPlacementClosed()
    {
        var userId = NewUserId();
        var campSeasonId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        await SeedMapSettingsAsync(placementOpen: false);
        SetupCityPlanningTeam(teamId, memberUserId: userId);

        var result = await _sut.CanUserEditAsync(userId, campSeasonId, Xunit.TestContext.Current.CancellationToken);
        result.Should().BeTrue();
    }

    private void SetupCityPlanningTeam(Guid teamId, Guid? memberUserId)
    {
        var members = memberUserId.HasValue
            ? new List<TeamMemberInfo>
            {
                new(Guid.NewGuid(), memberUserId.Value, string.Empty, null, null,
                    TeamMemberRole.Member, Instant.MinValue),
            }
            : new List<TeamMemberInfo>();
        var teamInfo = new TeamInfo(
            teamId, "City Planning", null, "city-planning",
            IsActive: true, IsSystemTeam: false, SystemTeamType: SystemTeamType.None,
            RequiresApproval: false, IsPublicPage: false, IsHidden: false,
            IsPromotedToDirectory: false, CreatedAt: Instant.MinValue,
            Members: members);
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, TeamInfo> { [teamId] = teamInfo });
    }

    [HumansFact]
    public async Task CanUserEditAsync_LeadWithPlacementOpen_ReturnsTrue()
    {
        var userId = NewUserId();
        var campId = Guid.NewGuid();
        var campSeasonId = Guid.NewGuid();
        await SeedMapSettingsAsync(placementOpen: true);

        _teamService.GetTeamEntityBySlugAsync("city-planning", Arg.Any<CancellationToken>())
            .Returns((Team?)null);

        _campService.GetCampSeasonByIdAsync(campSeasonId, Arg.Any<CancellationToken>())
            .Returns(MakeCampSeasonInfo(campSeasonId, campId, 2026, "Camp"));
        _campService.GetCampsForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns(new List<CampInfo>
            {
                MakeCampInfo(campId, "camp", [MakeCampSeasonInfo(campSeasonId, campId, 2026, "Camp") with
                {
                    LeadUserIds = [userId]
                }])
            });

        var result = await _sut.CanUserEditAsync(userId, campSeasonId, Xunit.TestContext.Current.CancellationToken);
        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task CanUserEditAsync_LeadWithPlacementClosed_ReturnsFalse()
    {
        var userId = NewUserId();
        var campId = Guid.NewGuid();
        var campSeasonId = Guid.NewGuid();
        await SeedMapSettingsAsync(placementOpen: false);

        _teamService.GetTeamEntityBySlugAsync("city-planning", Arg.Any<CancellationToken>())
            .Returns((Team?)null);

        _campService.GetCampSeasonByIdAsync(campSeasonId, Arg.Any<CancellationToken>())
            .Returns(MakeCampSeasonInfo(campSeasonId, campId, 2026, "Camp"));

        var result = await _sut.CanUserEditAsync(userId, campSeasonId, Xunit.TestContext.Current.CancellationToken);
        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task CanUserEditAsync_LeadOfDifferentCamp_ReturnsFalse()
    {
        var userId = NewUserId();
        var campId = Guid.NewGuid();
        var campSeasonId = Guid.NewGuid();
        await SeedMapSettingsAsync(placementOpen: true);

        _teamService.GetTeamEntityBySlugAsync("city-planning", Arg.Any<CancellationToken>())
            .Returns((Team?)null);

        _campService.GetCampSeasonByIdAsync(campSeasonId, Arg.Any<CancellationToken>())
            .Returns(MakeCampSeasonInfo(campSeasonId, campId, 2026, "Camp"));
        _campService.GetCampsForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns(new List<CampInfo>
            {
                MakeCampInfo(Guid.NewGuid(), "other-camp", [MakeCampSeasonInfo(Guid.NewGuid(), Guid.NewGuid(), 2026, "Other Camp") with
                {
                    LeadUserIds = [userId]
                }])
            });

        var result = await _sut.CanUserEditAsync(userId, campSeasonId, Xunit.TestContext.Current.CancellationToken);
        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task CanUserEditAsync_LeadOfDifferentYear_ReturnsFalse()
    {
        var userId = NewUserId();
        var campId = Guid.NewGuid();
        var campSeasonId = Guid.NewGuid();
        await SeedMapSettingsAsync(year: 2026, placementOpen: true);

        _teamService.GetTeamEntityBySlugAsync("city-planning", Arg.Any<CancellationToken>())
            .Returns((Team?)null);

        // Camp season is for 2027, but settings year is 2026
        _campService.GetCampSeasonByIdAsync(campSeasonId, Arg.Any<CancellationToken>())
            .Returns(MakeCampSeasonInfo(campSeasonId, campId, 2027, "Camp"));

        var result = await _sut.CanUserEditAsync(userId, campSeasonId, Xunit.TestContext.Current.CancellationToken);
        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task GetSettingsAsync_CreatesRowIfMissing()
    {
        SetupCampSettings(publicYear: 2026);

        var settings = await _sut.GetSettingsAsync(Xunit.TestContext.Current.CancellationToken);

        settings.Year.Should().Be(2026);
        settings.IsPlacementOpen.Should().BeFalse();
        (await Db.CityPlanningSettings.AsNoTracking().CountAsync(Xunit.TestContext.Current.CancellationToken)).Should().Be(1);
    }

    [HumansFact]
    public async Task GetSettingsAsync_ReturnsExistingRow()
    {
        var existing = await SeedMapSettingsAsync(year: 2026, placementOpen: true);

        var result = await _sut.GetSettingsAsync(Xunit.TestContext.Current.CancellationToken);

        result.Id.Should().Be(existing.Id);
        result.IsPlacementOpen.Should().BeTrue();
        (await Db.CityPlanningSettings.AsNoTracking().CountAsync(Xunit.TestContext.Current.CancellationToken)).Should().Be(1);
    }

    [HumansFact]
    public async Task OpenPlacementAsync_SetsIsPlacementOpenTrue()
    {
        await SeedMapSettingsAsync(placementOpen: false);
        var adminId = NewUserId();

        await _sut.OpenPlacementAsync(adminId, Xunit.TestContext.Current.CancellationToken);

        var settings = await Db.CityPlanningSettings.AsNoTracking().SingleAsync(Xunit.TestContext.Current.CancellationToken);
        settings.IsPlacementOpen.Should().BeTrue();
        settings.OpenedAt.Should().Be(Clock.GetCurrentInstant());
    }

    [HumansFact]
    public async Task ClosePlacementAsync_SetsIsPlacementOpenFalse()
    {
        await SeedMapSettingsAsync(placementOpen: true);
        var adminId = NewUserId();

        await _sut.ClosePlacementAsync(adminId, Xunit.TestContext.Current.CancellationToken);

        var settings = await Db.CityPlanningSettings.AsNoTracking().SingleAsync(Xunit.TestContext.Current.CancellationToken);
        settings.IsPlacementOpen.Should().BeFalse();
        settings.ClosedAt.Should().Be(Clock.GetCurrentInstant());
    }

    [HumansFact]
    public async Task GetCampPolygonsAsync_ReturnsOnlyPolygonsForYear()
    {
        var season2026Id = Guid.NewGuid();
        var season2027Id = Guid.NewGuid();
        var camp2026Id = Guid.NewGuid();
        var userId = NewUserId();

        await _sut.SaveCampPolygonAsync(season2026Id, """{"type":"Feature"}""", 100, userId, cancellationToken: Xunit.TestContext.Current.CancellationToken);
        await _sut.SaveCampPolygonAsync(season2027Id, """{"type":"Feature"}""", 200, userId, cancellationToken: Xunit.TestContext.Current.CancellationToken);

        _campService.GetCampsForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns(new List<CampInfo>
            {
                MakeCampInfo(camp2026Id, "test-camp", [MakeCampSeasonInfo(season2026Id, camp2026Id, 2026, "Test Camp 2026")])
            });

        var result = await _sut.GetCampPolygonsAsync(2026, Xunit.TestContext.Current.CancellationToken);

        result.Should().HaveCount(1);
        result[0].CampSeasonId.Should().Be(season2026Id);
        result[0].CampId.Should().Be(camp2026Id);
    }

    [HumansFact]
    public async Task GetCampSeasonsWithoutCampPolygonAsync_ExcludesSeasonsWithPolygon()
    {
        var campWithId = Guid.NewGuid();
        var campWithoutId = Guid.NewGuid();
        var seasonWithId = Guid.NewGuid();
        var seasonWithoutId = Guid.NewGuid();
        var userId = NewUserId();

        await _sut.SaveCampPolygonAsync(seasonWithId, """{"type":"Feature"}""", 100, userId, cancellationToken: Xunit.TestContext.Current.CancellationToken);

        _campService.GetCampsForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns(new List<CampInfo>
            {
                MakeCampInfo(campWithId, "camp-with", [MakeCampSeasonInfo(seasonWithId, campWithId, 2026, "Camp With")]),
                MakeCampInfo(campWithoutId, "camp-without", [MakeCampSeasonInfo(seasonWithoutId, campWithoutId, 2026, "Camp Without")])
            });

        var result = await _sut.GetCampSeasonsWithoutCampPolygonAsync(2026, Xunit.TestContext.Current.CancellationToken);

        result.Should().HaveCount(1);
        result[0].CampSeasonId.Should().Be(seasonWithoutId);
    }

    [HumansFact]
    public async Task ExportAsGeoJsonAsync_ReturnsFeatureCollection()
    {
        var campId = Guid.NewGuid();
        var campSeasonId = Guid.NewGuid();
        var userId = NewUserId();
        const string geoJson = """{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,0]]]},"properties":{}}""";

        await _sut.SaveCampPolygonAsync(campSeasonId, geoJson, 100.0, userId, cancellationToken: Xunit.TestContext.Current.CancellationToken);

        _campService.GetCampsForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns(new List<CampInfo>
            {
                MakeCampInfo(campId, "test-camp", [MakeCampSeasonInfo(campSeasonId, campId, 2026, "Test Camp")])
            });

        var result = await _sut.ExportAsGeoJsonAsync(2026, Xunit.TestContext.Current.CancellationToken);

        using var doc = System.Text.Json.JsonDocument.Parse(result);
        doc.RootElement.GetProperty("type").GetString().Should().Be("FeatureCollection");
        var features = doc.RootElement.GetProperty("features");
        features.GetArrayLength().Should().Be(1);
        features[0].GetProperty("properties").GetProperty("areaSqm").GetDouble().Should().Be(100.0);
    }

    [HumansFact]
    public async Task GetCampPolygonHistoryAsync_ReturnsEntries_WithDisplayNamesFromUserService()
    {
        var campSeasonId = Guid.NewGuid();
        var userId = NewUserId();

        // Stub the user service — replaces the old cross-domain .Include(h => h.ModifiedByUser).
        var testUser = new User { Id = userId, UserName = "test@test.com", Email = "test@test.com", DisplayName = "Test User" };
        _userService.GetByIdsAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(userId)),
            Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, User> { [userId] = testUser });
        _userService.GetUserInfosAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(userId)),
            Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(
                new Dictionary<Guid, UserInfo> { [userId] = testUser.ToUserInfo() }));

        Clock.Advance(Duration.FromSeconds(1));
        await _sut.SaveCampPolygonAsync(campSeasonId, """{"type":"Feature"}""", 100.0, userId, cancellationToken: Xunit.TestContext.Current.CancellationToken);
        Clock.Advance(Duration.FromSeconds(1));
        await _sut.SaveCampPolygonAsync(campSeasonId, """{"type":"Feature"}""", 200.0, userId, cancellationToken: Xunit.TestContext.Current.CancellationToken);

        var history = await _sut.GetCampPolygonHistoryAsync(campSeasonId, Xunit.TestContext.Current.CancellationToken);

        // Display ordering (newest first) moved to the controller
        // (CityPlanningApiController.GetCampPolygonHistory) per
        // memory/architecture/display-sort-in-controllers.md, so the service
        // only guarantees the entries and the display-name mapping, not order.
        history.Should().HaveCount(2);
        history.Select(h => h.AreaSqm).Should().BeEquivalentTo([100.0, 200.0]);
        history.Should().OnlyContain(h => h.ModifiedByDisplayName == "Test User");
    }

    [HumansFact(Timeout = 10000)]
    public async Task GetCampPolygonHistoryAsync_FallsBackToUserIdString_WhenUserNotFound()
    {
        var campSeasonId = Guid.NewGuid();
        var userId = NewUserId();

        // User service returns empty dictionary — user was deleted.
        _userService.GetByIdsAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(),
            Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, User>());
        _userService.GetUserInfosAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(),
            Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(new Dictionary<Guid, UserInfo>()));

        await _sut.SaveCampPolygonAsync(campSeasonId, """{"type":"Feature"}""", 100.0, userId, cancellationToken: Xunit.TestContext.Current.CancellationToken);

        var history = await _sut.GetCampPolygonHistoryAsync(campSeasonId, Xunit.TestContext.Current.CancellationToken);

        history.Should().HaveCount(1);
        history[0].ModifiedByDisplayName.Should().Be(userId.ToString());
    }

    [HumansFact]
    public async Task GetCampPolygonsAsync_IncludesSoundZone_WhenSet()
    {
        var campSeasonId = Guid.NewGuid();
        var campId = Guid.NewGuid();
        var userId = NewUserId();
        const string geoJson = """{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[]]}}""";
        await _sut.SaveCampPolygonAsync(campSeasonId, geoJson, 100.0, userId, cancellationToken: Xunit.TestContext.Current.CancellationToken);

        _campService.GetCampsForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns(new List<CampInfo>
            {
                MakeCampInfo(campId, "test-camp", [MakeCampSeasonInfo(campSeasonId, campId, 2026, "Test Camp", SoundZone.Blue)])
            });

        var polygons = await _sut.GetCampPolygonsAsync(2026, Xunit.TestContext.Current.CancellationToken);

        polygons.Single().SoundZone.Should().Be(SoundZone.Blue);
    }

    [HumansFact]
    public async Task GetCampPolygonsAsync_SoundZoneIsNull_WhenNotSet()
    {
        var campSeasonId = Guid.NewGuid();
        var campId = Guid.NewGuid();
        var userId = NewUserId();
        const string geoJson = """{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[]]}}""";
        await _sut.SaveCampPolygonAsync(campSeasonId, geoJson, 100.0, userId, cancellationToken: Xunit.TestContext.Current.CancellationToken);

        _campService.GetCampsForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns(new List<CampInfo>
            {
                MakeCampInfo(campId, "test-camp", [MakeCampSeasonInfo(campSeasonId, campId, 2026, "Test Camp")])
            });

        var polygons = await _sut.GetCampPolygonsAsync(2026, Xunit.TestContext.Current.CancellationToken);

        polygons.Single().SoundZone.Should().BeNull();
    }

    // --- UpdatePlacementDatesAsync ---

    [HumansFact]
    public async Task UpdatePlacementDatesAsync_SetsBothDates()
    {
        await SeedMapSettingsAsync();
        var opens = new LocalDateTime(2026, 4, 10, 18, 0);
        var closes = new LocalDateTime(2026, 4, 20, 23, 59);

        var result = await _sut.UpdatePlacementDatesAsync("2026-04-10T18:00", "2026-04-20T23:59", Xunit.TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        var settings = await Db.CityPlanningSettings.AsNoTracking().SingleAsync(Xunit.TestContext.Current.CancellationToken);
        settings.PlacementOpensAt.Should().Be(opens);
        settings.PlacementClosesAt.Should().Be(closes);
    }

    [HumansFact]
    public async Task UpdatePlacementDatesAsync_ClearsDates_WhenNull()
    {
        await SeedMapSettingsAsync();
        var seeded = await Db.CityPlanningSettings.SingleAsync(Xunit.TestContext.Current.CancellationToken);
        seeded.PlacementOpensAt = new LocalDateTime(2026, 4, 10, 18, 0);
        seeded.PlacementClosesAt = new LocalDateTime(2026, 4, 20, 23, 59);
        await Db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        Db.Entry(seeded).State = EntityState.Detached;

        var result = await _sut.UpdatePlacementDatesAsync(null, null, Xunit.TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        var updated = await Db.CityPlanningSettings.AsNoTracking().SingleAsync(Xunit.TestContext.Current.CancellationToken);
        updated.PlacementOpensAt.Should().BeNull();
        updated.PlacementClosesAt.Should().BeNull();
    }

    // --- UpdateOfficialZonesAsync / DeleteOfficialZonesAsync ---

    [HumansFact]
    public async Task UpdateOfficialZonesAsync_StoresGeoJson()
    {
        await SeedMapSettingsAsync();
        const string geoJson = """{"type":"FeatureCollection","features":[]}""";

        await _sut.UpdateOfficialZonesAsync(geoJson, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        var settings = await Db.CityPlanningSettings.AsNoTracking().SingleAsync(Xunit.TestContext.Current.CancellationToken);
        settings.OfficialZonesGeoJson.Should().Be(geoJson);
        settings.UpdatedAt.Should().Be(Clock.GetCurrentInstant());
    }

    [HumansFact]
    public async Task DeleteOfficialZonesAsync_SetsNull()
    {
        await SeedMapSettingsAsync();
        var seeded = await Db.CityPlanningSettings.SingleAsync(Xunit.TestContext.Current.CancellationToken);
        seeded.OfficialZonesGeoJson = """{"type":"FeatureCollection","features":[]}""";
        await Db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        Db.Entry(seeded).State = EntityState.Detached;

        await _sut.DeleteOfficialZonesAsync(Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        var updated = await Db.CityPlanningSettings.AsNoTracking().SingleAsync(Xunit.TestContext.Current.CancellationToken);
        updated.OfficialZonesGeoJson.Should().BeNull();
        updated.UpdatedAt.Should().Be(Clock.GetCurrentInstant());
    }

    private static CampSeasonInfo MakeCampSeasonInfo(Guid id, Guid campId, int year, string name, SoundZone? soundZone = null) =>
        new(id, campId, string.Empty, year, null, name, string.Empty, string.Empty,
            [], CampSeasonStatus.Pending, YesNoMaybe.No, YesNoMaybe.No, AdultPlayspacePolicy.No,
            0, soundZone, null, null, 0, null, null);

    private static CampInfo MakeCampInfo(Guid id, string slug, IReadOnlyList<CampSeasonInfo> seasons) =>
        new(id, slug, string.Empty, string.Empty, false, 0, seasons);

}
