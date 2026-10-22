using Xunit;
using AwesomeAssertions;
using Humans.CityPlanning.Contracts;
using Humans.AuditLog.Contracts;
using Humans.Camps.Contracts;
using Humans.Teams.Contracts;
using Humans.CityPlanning.Services;

using Humans.CityPlanning.Domain;
using Humans.Base.Enums;
using Humans.CityPlanning.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using NSubstitute;
using Humans.Settings.Contracts;
using Humans.Users.Contracts;

namespace Humans.CityPlanning.Tests;

public sealed class CityPlanningServiceTests : CityPlanningTestBase
{
    private readonly ICampService _campService;
    private readonly ITeamService _teamService;
    private readonly IUserService _userService;
    private readonly IAuditLogService _auditLog;
    private readonly ISettingsService _settingsService = ActiveEvent(2026);
    private readonly CityPlanningService _sut;
    private readonly CityPlanningOptions _options = new() { CityPlanningTeamSlug = "city-planning" };

    public CityPlanningServiceTests()
        : base(Instant.FromUtc(2026, 3, 15, 12, 0, 0))
    {
        _campService = Substitute.For<ICampService>();
        _teamService = Substitute.For<ITeamService>();
        _userService = Substitute.For<IUserService>();
        _auditLog = Substitute.For<IAuditLogService>();
        var repo = new CityPlanningRepository(CityPlanningDbFactory);
        _sut = new CityPlanningService(
            repo, Clock, Options.Create(_options),
            _campService, _teamService, _userService, _auditLog, _settingsService);
    }

    /// <summary>Camps' public year and the active event's year — city planning's current year — both <paramref name="publicYear"/>.</summary>
    private void SetupCampSettings(int publicYear = 2026)
    {
        _campService.GetSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(new CampSettingsInfo(publicYear, []));
        SetActiveEventYear(publicYear);
    }

    private void SetActiveEventYear(int? year) =>
        _settingsService.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(year is { } y ? BurnFixtures.Burn(year: y) : null);

    private async Task<CityPlanningSettings> SeedMapSettingsAsync(int year = 2026, bool placementOpen = false)
    {
        SetupCampSettings(year);
        var settings = new CityPlanningSettings
        {
            Year = year,
            PlacementOverride = placementOpen ? true : null,
            UpdatedAt = Clock.GetCurrentInstant()
        };
        CityPlanningDb.CityPlanningSettings.Add(settings);
        await CityPlanningDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        return settings;
    }

    private static Guid NewUserId() => Guid.NewGuid();

    private static IFormFile CreateUpload(string content)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "upload.geojson");
    }

    // The size guard reads Length, never the stream, so this stays cheap.
    private static IFormFile CreateOversizedUpload() =>
        new FormFile(new MemoryStream([1]), 0, (10 * 1024 * 1024) + 1, "file", "huge.geojson");

    [HumansTheory]
    [InlineData(0.0)]
    [InlineData(500.0)]
    public async Task SaveCampPolygonAsync_FirstSave_CreatesBothPolygonAndHistory(double areaSqm)
    {
        var campSeasonId = Guid.NewGuid();
        var userId = NewUserId();
        const string geoJson = """{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,0]]]}}""";

        await _sut.SaveCampPolygonAsync(campSeasonId, geoJson, areaSqm, userId, cancellationToken: TestContext.Current.CancellationToken);

        var polygon = await CityPlanningDb.CampPolygons.AsNoTracking().SingleAsync(p => p.CampSeasonId == campSeasonId, TestContext.Current.CancellationToken);
        var history = await CityPlanningDb.CampPolygonHistories.AsNoTracking().SingleAsync(h => h.CampSeasonId == campSeasonId, TestContext.Current.CancellationToken);

        polygon.GeoJson.Should().Be(geoJson);
        polygon.AreaSqm.Should().Be(areaSqm);
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

        await _sut.SaveCampPolygonAsync(campSeasonId, geoJson1, 100.0, userId, cancellationToken: TestContext.Current.CancellationToken);
        await _sut.SaveCampPolygonAsync(campSeasonId, geoJson2, 200.0, userId, cancellationToken: TestContext.Current.CancellationToken);

        var polygonCount = await CityPlanningDb.CampPolygons.AsNoTracking().CountAsync(p => p.CampSeasonId == campSeasonId, TestContext.Current.CancellationToken);
        var historyCount = await CityPlanningDb.CampPolygonHistories.AsNoTracking().CountAsync(h => h.CampSeasonId == campSeasonId, TestContext.Current.CancellationToken);
        var polygon = await CityPlanningDb.CampPolygons.AsNoTracking().SingleAsync(p => p.CampSeasonId == campSeasonId, TestContext.Current.CancellationToken);

        polygonCount.Should().Be(1);
        historyCount.Should().Be(2);
        polygon.GeoJson.Should().Be(geoJson2);
        polygon.AreaSqm.Should().Be(200.0);
    }

    [HumansTheory]
    [InlineData("{not-json")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("123")]
    [InlineData("\"polygon\"")]
    [InlineData("""{"type":"Feature"}""")]
    [InlineData("""{"type":"Feature","geometry":null}""")]
    [InlineData("""{"type":"Point","coordinates":[0,0]}""")]
    [InlineData("""{"type":"FeatureCollection","features":[]}""")]
    [InlineData("""{"type":"Polygon","coordinates":[]}""")]
    [InlineData("""{"type":"Polygon","coordinates":[[]]}""")]
    [InlineData("""{"type":"Polygon","coordinates":[[[0,0],[1,0],[0,0]]]}""")]
    [InlineData("""{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[2,2]]]}""")]
    [InlineData("""{"type":"Polygon","coordinates":[[[0],[1,0],[1,1],[0]]]}""")]
    [InlineData("""{"type":"Polygon","coordinates":[[[0,0],[1e400,0],[1,1],[0,0]]]}""")]
    [InlineData("""{"type":"Polygon","coordinates":[[[0,0],[1,91],[1,1],[0,0]]]}""")]
    [InlineData("""{"type":"Polygon","coordinates":[[[0,0],[181,0],[1,1],[0,0]]]}""")]
    [InlineData("""{"type":"Polygon","coordinates":[[[0,0],["1",0],[1,1],[0,0]]]}""")]
    [InlineData("""{"type":"MultiPolygon","coordinates":[[]]}""")]
    public async Task SaveCampPolygonAsync_InvalidGeoJson_Throws(string geoJson)
    {
        var act = async () => await _sut.SaveCampPolygonAsync(
            Guid.NewGuid(), geoJson, 100.0, NewUserId(), cancellationToken: TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("Invalid GeoJSON.*");
        (await CityPlanningDb.CampPolygons.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
        (await CityPlanningDb.CampPolygonHistories.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [HumansTheory]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public async Task SaveCampPolygonAsync_InvalidArea_RejectsWithoutSaving(double areaSqm)
    {
        const string geoJson = """{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,0]]]}""";
        var act = async () => await _sut.SaveCampPolygonAsync(
            Guid.NewGuid(), geoJson, areaSqm, NewUserId(), cancellationToken: TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("Area must be finite and non-negative.*");
        (await CityPlanningDb.CampPolygons.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
        (await CityPlanningDb.CampPolygonHistories.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [HumansTheory]
    [InlineData("""{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0.0,0.0]]]}""")]
    [InlineData("""{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[[0,0],[4,0],[4,4],[0,0]],[[1,1],[2,1],[2,2],[1,1]]]}}""")]
    [InlineData("""{"type":"Polygon","coordinates":[[[0,0,3],[1,0,3],[1,1,3],[0,0,3]]]}""")]
    [InlineData("""{"type":"MultiPolygon","coordinates":[[[[0,0],[1,0],[1,1],[0,0]]],[[[2,2],[3,2],[3,3],[2,2]]]]}""")]
    [InlineData("""{"type":"Feature","geometry":{"type":"MultiPolygon","coordinates":[[[[0,0],[1,0],[1,1],[0,0]]]]}}""")]
    public async Task SaveCampPolygonAsync_ValidAreaGeometry_RoundTrips(string geoJson)
    {
        var saved = await _sut.SaveCampPolygonAsync(Guid.NewGuid(), geoJson, 100, NewUserId(), cancellationToken: TestContext.Current.CancellationToken);
        saved.GeoJson.Should().Be(geoJson);
        (await CityPlanningDb.CampPolygons.SingleAsync(TestContext.Current.CancellationToken)).GeoJson.Should().Be(geoJson);
        (await CityPlanningDb.CampPolygonHistories.SingleAsync(TestContext.Current.CancellationToken)).GeoJson.Should().Be(geoJson);
    }

    [HumansFact]
    public async Task UpdateLimitZoneFromUploadAsync_ValidFile_PersistsLimitZone()
    {
        await SeedMapSettingsAsync();
        var file = CreateUpload("""{"type":"FeatureCollection","features":[]}""");

        var result = await _sut.UpdateLimitZoneFromUploadAsync(file, NewUserId(), TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        var settings = await CityPlanningDb.CityPlanningSettings.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        settings.LimitZoneGeoJson.Should().Be("""{"type":"FeatureCollection","features":[]}""");
    }

    [HumansFact]
    public async Task UpdateLimitZoneFromUploadAsync_InvalidJson_ReturnsError()
    {
        var file = CreateUpload("{not-json");

        var result = await _sut.UpdateLimitZoneFromUploadAsync(file, NewUserId(), TestContext.Current.CancellationToken);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("InvalidGeoJson");
    }

    [HumansFact]
    public async Task UpdateLimitZoneFromUploadAsync_NoFile_ReturnsMissingFile()
    {
        var result = await _sut.UpdateLimitZoneFromUploadAsync(null, NewUserId(), TestContext.Current.CancellationToken);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("MissingFile");
    }

    [HumansFact]
    public async Task UpdateLimitZoneFromUploadAsync_EmptyFile_ReturnsMissingFile()
    {
        var result = await _sut.UpdateLimitZoneFromUploadAsync(
            CreateUpload(""), NewUserId(), TestContext.Current.CancellationToken);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("MissingFile");
    }

    [HumansFact]
    public async Task UpdateLimitZoneFromUploadAsync_OverTenMegabytes_ReturnsFileTooLarge()
    {
        var result = await _sut.UpdateLimitZoneFromUploadAsync(
            CreateOversizedUpload(), NewUserId(), TestContext.Current.CancellationToken);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("FileTooLarge");
    }

    [HumansFact]
    public async Task UpdateOfficialZonesFromUploadAsync_NoFile_ReturnsMissingFile()
    {
        var result = await _sut.UpdateOfficialZonesFromUploadAsync(null, NewUserId(), TestContext.Current.CancellationToken);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("MissingFile");
    }

    [HumansFact]
    public async Task UpdateOfficialZonesFromUploadAsync_OverTenMegabytes_ReturnsFileTooLarge()
    {
        var result = await _sut.UpdateOfficialZonesFromUploadAsync(
            CreateOversizedUpload(), NewUserId(), TestContext.Current.CancellationToken);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("FileTooLarge");
    }

    [HumansFact]
    public async Task UpdateOfficialZonesFromUploadAsync_ValidFile_PersistsOfficialZones()
    {
        await SeedMapSettingsAsync();
        var file = CreateUpload("""{"type":"FeatureCollection","features":[]}""");

        var result = await _sut.UpdateOfficialZonesFromUploadAsync(file, NewUserId(), TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        var settings = await CityPlanningDb.CityPlanningSettings.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        settings.OfficialZonesGeoJson.Should().Be("""{"type":"FeatureCollection","features":[]}""");
    }

    [HumansFact]
    public async Task RestoreCampPolygonVersionAsync_RestoresGeoJsonWithNote()
    {
        var campSeasonId = Guid.NewGuid();
        var userId = NewUserId();
        const string originalGeoJson = """{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,0]]]}}""";

        await _sut.SaveCampPolygonAsync(campSeasonId, originalGeoJson, 100.0, userId, cancellationToken: TestContext.Current.CancellationToken);
        var originalHistory = await CityPlanningDb.CampPolygonHistories.AsNoTracking()
            .SingleAsync(h => h.CampSeasonId == campSeasonId, TestContext.Current.CancellationToken);
        Clock.Advance(Duration.FromSeconds(1));
        await _sut.SaveCampPolygonAsync(campSeasonId, """{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[[0,0],[5,0],[5,5],[0,0]]]}}""", 999.0, userId, cancellationToken: TestContext.Current.CancellationToken);
        Clock.Advance(Duration.FromSeconds(1));

        await _sut.RestoreCampPolygonVersionAsync(campSeasonId, originalHistory.Id, userId, TestContext.Current.CancellationToken);

        var polygon = await CityPlanningDb.CampPolygons.AsNoTracking().SingleAsync(p => p.CampSeasonId == campSeasonId, TestContext.Current.CancellationToken);
        var latestHistory = await CityPlanningDb.CampPolygonHistories.AsNoTracking()
            .OrderByDescending(h => h.ModifiedAt).FirstAsync(h => h.CampSeasonId == campSeasonId, TestContext.Current.CancellationToken);

        polygon.GeoJson.Should().Be(originalGeoJson);
        latestHistory.Note.Should().StartWith("Restored from");
    }

    [HumansFact]
    public async Task IsCityPlanningTeamMemberAsync_TeamMember_ReturnsTrue()
    {
        var userId = NewUserId();
        var teamId = Guid.NewGuid();
        SetupCityPlanningTeam(teamId, memberUserId: userId);

        var result = await _sut.IsCityPlanningTeamMemberAsync(userId, TestContext.Current.CancellationToken);
        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task IsCityPlanningTeamMemberAsync_NonMember_ReturnsFalse()
    {
        var userId = NewUserId();
        var teamId = Guid.NewGuid();
        SetupCityPlanningTeam(teamId, memberUserId: null);

        var result = await _sut.IsCityPlanningTeamMemberAsync(userId, TestContext.Current.CancellationToken);
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

        var result = await _sut.CanUserEditAsync(userId, campSeasonId, TestContext.Current.CancellationToken);
        result.Should().BeTrue();
    }

    // The settings row records when a phase or zone changed, never who. These writes are
    // organisers acting on the members' behalf, so the actor lives in the audit log.

    [HumansFact]
    public async Task OpenPlacementAsync_WritesAuditEntry_NamingTheActor()
    {
        var settings = await SeedMapSettingsAsync();
        var userId = NewUserId();

        await _sut.OpenPlacementAsync(userId, TestContext.Current.CancellationToken);

        await _auditLog.Received(1).LogAsync(
            AuditAction.CityPlanningPlacementOpened,
            nameof(CityPlanningSettings),
            settings.Id,
            Arg.Any<string>(),
            userId,
            Arg.Any<Guid?>(),
            Arg.Any<string?>());
    }

    [HumansFact]
    public async Task ClosePlacementAsync_WritesAuditEntry()
    {
        await SeedMapSettingsAsync(placementOpen: true);
        var userId = NewUserId();

        await _sut.ClosePlacementAsync(userId, TestContext.Current.CancellationToken);

        await _auditLog.Received(1).LogAsync(
            AuditAction.CityPlanningPlacementClosed,
            Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(), userId,
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task UpdateRegistrationInfoAsync_AuditsAgainstTheOpenSeasonRow()
    {
        _campService.GetSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(new CampSettingsInfo(2026, [2027]));
        var userId = NewUserId();

        await _sut.UpdateRegistrationInfoAsync("Bring water.", userId, TestContext.Current.CancellationToken);

        var row = await CityPlanningDb.CityPlanningSettings.AsNoTracking()
            .SingleAsync(s => s.Year == 2027, TestContext.Current.CancellationToken);
        await _auditLog.Received(1).LogAsync(
            AuditAction.CityPlanningRegistrationInfoUpdated,
            nameof(CityPlanningSettings),
            row.Id,
            Arg.Any<string>(),
            userId,
            Arg.Any<Guid?>(),
            Arg.Any<string?>());
    }

    [HumansFact]
    public async Task ContainerPlacementToggles_WriteTheirOwnAuditActions()
    {
        await SeedMapSettingsAsync();
        var userId = NewUserId();

        await _sut.OpenContainerPlacementAsync(userId, TestContext.Current.CancellationToken);
        await _sut.CloseContainerPlacementAsync(userId, TestContext.Current.CancellationToken);

        await _auditLog.Received(1).LogAsync(
            AuditAction.CityPlanningContainerPlacementOpened,
            Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(), userId,
            Arg.Any<Guid?>(), Arg.Any<string?>());
        await _auditLog.Received(1).LogAsync(
            AuditAction.CityPlanningContainerPlacementClosed,
            Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(), userId,
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task ZoneUploadAndDelete_WriteTheirOwnAuditActions()
    {
        await SeedMapSettingsAsync();
        var userId = NewUserId();
        var file = CreateUpload("""{"type":"FeatureCollection","features":[]}""");

        await _sut.UpdateOfficialZonesFromUploadAsync(file, userId, TestContext.Current.CancellationToken);
        await _sut.DeleteLimitZoneAsync(userId, TestContext.Current.CancellationToken);

        await _auditLog.Received(1).LogAsync(
            AuditAction.CityPlanningOfficialZonesUpdated,
            Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(), userId,
            Arg.Any<Guid?>(), Arg.Any<string?>());
        await _auditLog.Received(1).LogAsync(
            AuditAction.CityPlanningLimitZoneDeleted,
            Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(), userId,
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    // A rejected upload never reaches the settings row, so it must not leave an audit entry.
    [HumansFact]
    public async Task UpdateLimitZoneFromUploadAsync_RejectedFile_WritesNoAuditEntry()
    {
        await SeedMapSettingsAsync();

        var result = await _sut.UpdateLimitZoneFromUploadAsync(
            CreateUpload("{not-json"), NewUserId(), TestContext.Current.CancellationToken);

        result.Success.Should().BeFalse();
        await _auditLog.DidNotReceive().LogAsync(
            AuditAction.CityPlanningLimitZoneUpdated,
            Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    // IAuditLogService takes no cancellation token, so the settings row's id is resolved before
    // the write: re-read after it, an abort mid-write committed the change unaudited.
    [HumansFact]
    public async Task OpenPlacementAsync_RequestCancelledDuringTheSave_StillWritesAuditEntry()
    {
        SetupCampSettings();
        var settingsId = Guid.NewGuid();
        var userId = NewUserId();
        using var cts = new CancellationTokenSource();

        var repo = Substitute.For<ICityPlanningRepository>();
        repo.GetOrCreateSettingsAsync(Arg.Any<int>(), Arg.Any<Instant>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                ci.Arg<CancellationToken>().ThrowIfCancellationRequested();
                return new CityPlanningSettings { Id = settingsId, Year = 2026 };
            });
        repo.MutateSettingsAsync(
                Arg.Any<int>(), Arg.Any<Action<CityPlanningSettings>>(),
                Arg.Any<Instant>(), Arg.Any<CancellationToken>())
            .Returns(_ => cts.CancelAsync());

        var sut = new CityPlanningService(
            repo, Clock, Options.Create(_options),
            _campService, _teamService, _userService, _auditLog, _settingsService);

        await sut.OpenPlacementAsync(userId, cts.Token);

        await _auditLog.Received(1).LogAsync(
            AuditAction.CityPlanningPlacementOpened,
            nameof(CityPlanningSettings),
            settingsId,
            Arg.Any<string>(),
            userId,
            Arg.Any<Guid?>(),
            Arg.Any<string?>());
    }

    // The repository passes its token into SaveChangesAsync, so a request aborted while that
    // command is in flight can leave the row committed while the await reports cancellation —
    // the change lands and LogAsync, which takes no token, is never reached. The audited write
    // therefore runs on CancellationToken.None: here the token is already cancelled by the time
    // the mutation is issued, and a repository that honours its token still commits and audits.
    [HumansFact]
    public async Task OpenPlacementAsync_RequestAlreadyAbortedWhenTheWriteIsIssued_StillWritesAuditEntry()
    {
        SetupCampSettings();
        var settingsId = Guid.NewGuid();
        var userId = NewUserId();
        using var cts = new CancellationTokenSource();

        var repo = Substitute.For<ICityPlanningRepository>();
        repo.GetOrCreateSettingsAsync(Arg.Any<int>(), Arg.Any<Instant>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                // The abort arrives after the read and before the write — the window the
                // production repository would carry into SaveChangesAsync.
                await cts.CancelAsync();
                return new CityPlanningSettings { Id = settingsId, Year = 2026 };
            });
        repo.MutateSettingsAsync(
                Arg.Any<int>(), Arg.Any<Action<CityPlanningSettings>>(),
                Arg.Any<Instant>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                ci.Arg<CancellationToken>().ThrowIfCancellationRequested();
                return Task.CompletedTask;
            });

        var sut = new CityPlanningService(
            repo, Clock, Options.Create(_options),
            _campService, _teamService, _userService, _auditLog, _settingsService);

        await sut.OpenPlacementAsync(userId, cts.Token);

        await repo.Received(1).MutateSettingsAsync(
            Arg.Any<int>(), Arg.Any<Action<CityPlanningSettings>>(),
            Arg.Any<Instant>(), Arg.Is<CancellationToken>(t => !t.CanBeCanceled));
        await _auditLog.Received(1).LogAsync(
            AuditAction.CityPlanningPlacementOpened,
            nameof(CityPlanningSettings),
            settingsId,
            Arg.Any<string>(),
            userId,
            Arg.Any<Guid?>(),
            Arg.Any<string?>());
    }

    // The slug is normalized on both sides: a stored slug carrying uppercase must still match,
    // or every city-planning team member silently loses their map-admin exemption.
    [HumansFact]
    public async Task IsCityPlanningTeamMemberAsync_StoredSlugHasUppercase_StillMatches()
    {
        var userId = NewUserId();
        SetupCityPlanningTeam(Guid.NewGuid(), memberUserId: userId, slug: "City-Planning");

        var result = await _sut.IsCityPlanningTeamMemberAsync(userId, TestContext.Current.CancellationToken);
        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task IsCityPlanningTeamMemberAsync_ConfiguredSlugHasUppercase_StillMatches()
    {
        var userId = NewUserId();
        _options.CityPlanningTeamSlug = "City-Planning";
        SetupCityPlanningTeam(Guid.NewGuid(), memberUserId: userId);

        var result = await _sut.IsCityPlanningTeamMemberAsync(userId, TestContext.Current.CancellationToken);
        result.Should().BeTrue();
    }

    // Normalizing both sides turns null into the empty string, so a blank configured slug
    // would otherwise match any team whose CustomSlug is null.
    [HumansFact]
    public async Task IsCityPlanningTeamMemberAsync_BlankConfiguredSlug_MatchesNothing()
    {
        var userId = NewUserId();
        _options.CityPlanningTeamSlug = "   ";
        SetupCityPlanningTeam(Guid.NewGuid(), memberUserId: userId);

        var result = await _sut.IsCityPlanningTeamMemberAsync(userId, TestContext.Current.CancellationToken);
        result.Should().BeFalse();
    }

    private void SetupCityPlanningTeam(Guid teamId, Guid? memberUserId, string slug = "city-planning")
    {
        var members = memberUserId.HasValue
            ? new List<TeamMemberInfo>
            {
                new(Guid.NewGuid(), memberUserId.Value, string.Empty, null, null,
                    TeamMemberRole.Member, Instant.MinValue),
            }
            : new List<TeamMemberInfo>();
        var teamInfo = new TeamInfo(
            teamId, "City Planning", null, slug,
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

        _teamService.GetTeamBySlugAsync("city-planning", Arg.Any<CancellationToken>())
            .Returns((TeamInfo?)null);

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

        var result = await _sut.CanUserEditAsync(userId, campSeasonId, TestContext.Current.CancellationToken);
        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task CanUserEditAsync_LeadWithPlacementClosed_ReturnsFalse()
    {
        var userId = NewUserId();
        var campId = Guid.NewGuid();
        var campSeasonId = Guid.NewGuid();
        await SeedMapSettingsAsync(placementOpen: false);

        _teamService.GetTeamBySlugAsync("city-planning", Arg.Any<CancellationToken>())
            .Returns((TeamInfo?)null);

        _campService.GetCampSeasonByIdAsync(campSeasonId, Arg.Any<CancellationToken>())
            .Returns(MakeCampSeasonInfo(campSeasonId, campId, 2026, "Camp"));

        var result = await _sut.CanUserEditAsync(userId, campSeasonId, TestContext.Current.CancellationToken);
        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task CanUserEditAsync_LeadOfDifferentCamp_ReturnsFalse()
    {
        var userId = NewUserId();
        var campId = Guid.NewGuid();
        var campSeasonId = Guid.NewGuid();
        await SeedMapSettingsAsync(placementOpen: true);

        _teamService.GetTeamBySlugAsync("city-planning", Arg.Any<CancellationToken>())
            .Returns((TeamInfo?)null);

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

        var result = await _sut.CanUserEditAsync(userId, campSeasonId, TestContext.Current.CancellationToken);
        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task CanUserEditAsync_LeadOfDifferentYear_ReturnsFalse()
    {
        var userId = NewUserId();
        var campId = Guid.NewGuid();
        var campSeasonId = Guid.NewGuid();
        await SeedMapSettingsAsync(year: 2026, placementOpen: true);

        _teamService.GetTeamBySlugAsync("city-planning", Arg.Any<CancellationToken>())
            .Returns((TeamInfo?)null);

        // Camp season is for 2027, but settings year is 2026
        _campService.GetCampSeasonByIdAsync(campSeasonId, Arg.Any<CancellationToken>())
            .Returns(MakeCampSeasonInfo(campSeasonId, campId, 2027, "Camp"));

        var result = await _sut.CanUserEditAsync(userId, campSeasonId, TestContext.Current.CancellationToken);
        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task GetSettingsAsync_NoRow_ReturnsUnpersistedDefaultsAndWritesNothing()
    {
        SetupCampSettings(publicYear: 2026);

        var settings = await _sut.GetSettingsAsync(TestContext.Current.CancellationToken);

        settings.Should().NotBeNull();
        settings!.Id.Should().Be(Guid.Empty);
        settings.Year.Should().Be(2026);
        settings.PlacementState.Should().Be(BarrioPlacementState.Closed);
        settings.PlacementOverride.Should().BeNull();
        settings.IsContainerPlacementOpen.Should().BeFalse();
        settings.PlacementOpensOffset.Should().BeNull();
        settings.PlacementOpensOn.Should().BeNull();
        (await CityPlanningDb.CityPlanningSettings.AsNoTracking().CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [HumansFact]
    public async Task GetRegistrationInfoAsync_NoRow_WritesNothing()
    {
        SetupCampSettings(publicYear: 2026);

        (await _sut.GetRegistrationInfoAsync(TestContext.Current.CancellationToken)).Should().BeNull();

        (await CityPlanningDb.CityPlanningSettings.AsNoTracking().CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [HumansFact]
    public async Task GetSettingsAsync_YearFollowsTheActiveEvent_NotCampsPublicYear()
    {
        _campService.GetSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(new CampSettingsInfo(2026, []));
        SetActiveEventYear(2027);
        CityPlanningDb.CityPlanningSettings.Add(new CityPlanningSettings
        {
            Year = 2027,
            PlacementOverride = true,
            UpdatedAt = Clock.GetCurrentInstant()
        });
        await CityPlanningDb.SaveChangesAsync(TestContext.Current.CancellationToken);

        var settings = await _sut.GetSettingsAsync(TestContext.Current.CancellationToken);

        settings!.Year.Should().Be(2027);
        settings.PlacementState.Should().Be(BarrioPlacementState.Open);
    }

    [HumansFact]
    public async Task GetSettingsAsync_NoActiveEvent_ReturnsNullAndWritesNothing()
    {
        SetActiveEventYear(null);

        (await _sut.GetSettingsAsync(TestContext.Current.CancellationToken)).Should().BeNull();

        (await CityPlanningDb.CityPlanningSettings.AsNoTracking().CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [HumansFact]
    public async Task OpenPlacementAsync_NoRow_FirstWriteCreatesTheActiveEventYearsRow()
    {
        SetupCampSettings(publicYear: 2026);
        SetActiveEventYear(2027);

        await _sut.OpenPlacementAsync(NewUserId(), TestContext.Current.CancellationToken);

        var row = await CityPlanningDb.CityPlanningSettings.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        row.Year.Should().Be(2027);
        row.PlacementOverride.Should().BeTrue();
        (await _sut.GetSettingsAsync(TestContext.Current.CancellationToken))!.Id.Should().Be(row.Id);
    }

    [HumansFact]
    public async Task OpenPlacementAsync_NoActiveEvent_ThrowsAndWritesNothing()
    {
        SetActiveEventYear(null);

        var act = () => _sut.OpenPlacementAsync(NewUserId(), TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<InvalidOperationException>();

        (await CityPlanningDb.CityPlanningSettings.AsNoTracking().CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [HumansFact]
    public async Task GetSettingsAsync_ReturnsExistingRow()
    {
        var existing = await SeedMapSettingsAsync(year: 2026, placementOpen: true);

        var result = (await _sut.GetSettingsAsync(TestContext.Current.CancellationToken))!;

        result.Id.Should().Be(existing.Id);
        result.PlacementState.Should().Be(BarrioPlacementState.Open);
        (await CityPlanningDb.CityPlanningSettings.AsNoTracking().CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
    }

    [HumansFact]
    public async Task GetSettingsAsync_ExplicitYear_ReturnsThatYearsPhaseInsteadOfTheActiveEventYear()
    {
        SetupCampSettings(publicYear: 2026);
        await SeedMapSettingsAsync(year: 2026, placementOpen: true);
        var existing = await SeedMapSettingsAsync(year: 2025, placementOpen: false);

        var result = (await _sut.GetSettingsAsync(TestContext.Current.CancellationToken, 2025))!;

        result.Id.Should().Be(existing.Id);
        result.Year.Should().Be(2025);
        result.PlacementState.Should().Be(BarrioPlacementState.Closed);
        (await CityPlanningDb.CityPlanningSettings.AsNoTracking().CountAsync(TestContext.Current.CancellationToken)).Should().Be(2);
    }

    [HumansFact]
    public async Task OpenPlacementAsync_ForcesTheOverrideOpen()
    {
        await SeedMapSettingsAsync(placementOpen: false);
        var adminId = NewUserId();

        await _sut.OpenPlacementAsync(adminId, TestContext.Current.CancellationToken);

        var settings = await CityPlanningDb.CityPlanningSettings.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        settings.PlacementOverride.Should().BeTrue();
        settings.OpenedAt.Should().Be(Clock.GetCurrentInstant());
    }

    [HumansFact]
    public async Task ClosePlacementAsync_ForcesTheOverrideClosed()
    {
        await SeedMapSettingsAsync(placementOpen: true);
        var adminId = NewUserId();

        await _sut.ClosePlacementAsync(adminId, TestContext.Current.CancellationToken);

        var settings = await CityPlanningDb.CityPlanningSettings.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        settings.PlacementOverride.Should().BeFalse();
        settings.ClosedAt.Should().Be(Clock.GetCurrentInstant());
    }

    [HumansFact]
    public async Task GetCampPolygonsAsync_ReturnsOnlyPolygonsForYear()
    {
        var season2026Id = Guid.NewGuid();
        var season2027Id = Guid.NewGuid();
        var camp2026Id = Guid.NewGuid();
        var userId = NewUserId();

        await _sut.SaveCampPolygonAsync(season2026Id, """{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,0]]]}}""", 100, userId, cancellationToken: TestContext.Current.CancellationToken);
        await _sut.SaveCampPolygonAsync(season2027Id, """{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,0]]]}}""", 200, userId, cancellationToken: TestContext.Current.CancellationToken);

        _campService.GetCampsForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns(new List<CampInfo>
            {
                MakeCampInfo(camp2026Id, "test-camp", [MakeCampSeasonInfo(season2026Id, camp2026Id, 2026, "Test Camp 2026")])
            });

        var result = await _sut.GetCampPolygonsAsync(2026, TestContext.Current.CancellationToken);

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

        await _sut.SaveCampPolygonAsync(seasonWithId, """{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,0]]]}}""", 100, userId, cancellationToken: TestContext.Current.CancellationToken);

        _campService.GetCampsForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns(new List<CampInfo>
            {
                MakeCampInfo(campWithId, "camp-with", [MakeCampSeasonInfo(seasonWithId, campWithId, 2026, "Camp With")]),
                MakeCampInfo(campWithoutId, "camp-without", [MakeCampSeasonInfo(seasonWithoutId, campWithoutId, 2026, "Camp Without")])
            });

        var result = await _sut.GetCampSeasonsWithoutCampPolygonAsync(2026, TestContext.Current.CancellationToken);

        result.Should().HaveCount(1);
        result[0].CampSeasonId.Should().Be(seasonWithoutId);
    }

    [HumansTheory]
    [InlineData("""{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,0]]]},"properties":{}}""")]
    [InlineData("""{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,0]]]}""")]
    public async Task ExportAsGeoJsonAsync_ReturnsFeatureCollection(string geoJson)
    {
        var campId = Guid.NewGuid();
        var campSeasonId = Guid.NewGuid();
        var userId = NewUserId();

        await _sut.SaveCampPolygonAsync(campSeasonId, geoJson, 100.0, userId, cancellationToken: TestContext.Current.CancellationToken);

        _campService.GetCampsForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns(new List<CampInfo>
            {
                MakeCampInfo(campId, "test-camp", [MakeCampSeasonInfo(campSeasonId, campId, 2026, "Test Camp")])
            });

        var result = await _sut.ExportAsGeoJsonAsync(2026, TestContext.Current.CancellationToken);

        using var doc = System.Text.Json.JsonDocument.Parse(result);
        doc.RootElement.GetProperty("type").GetString().Should().Be("FeatureCollection");
        var features = doc.RootElement.GetProperty("features");
        features.GetArrayLength().Should().Be(1);
        features[0].GetProperty("geometry").GetProperty("type").GetString().Should().Be("Polygon");
        features[0].GetProperty("properties").GetProperty("areaSqm").GetDouble().Should().Be(100.0);
    }

    [HumansFact]
    public async Task GetCampPolygonHistoryAsync_ReturnsEntries_WithDisplayNamesFromUserService()
    {
        var campSeasonId = Guid.NewGuid();
        var userId = NewUserId();

        // UserInfo.BurnerName reads User.BurnerName, so it is seeded there.
        var testUser = new User { Id = userId, UserName = "test@test.com", Email = "test@test.com", DisplayName = "Test User", BurnerName = "Test User" };
        _userService.GetUserInfosAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(userId)),
            Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(
                // UserInfo.Create inline rather than linking Base's UserInfoStubHelpers: this
                // suite needs one of its members, and the helper is built around UsersDbContext.
                new Dictionary<Guid, UserInfo>
                {
                    [userId] = UserInfo.Create(testUser, [], [], [], profile: null, []),
                }));

        Clock.Advance(Duration.FromSeconds(1));
        await _sut.SaveCampPolygonAsync(campSeasonId, """{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,0]]]}}""", 100.0, userId, cancellationToken: TestContext.Current.CancellationToken);
        Clock.Advance(Duration.FromSeconds(1));
        await _sut.SaveCampPolygonAsync(campSeasonId, """{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,0]]]}}""", 200.0, userId, cancellationToken: TestContext.Current.CancellationToken);

        var history = await _sut.GetCampPolygonHistoryAsync(campSeasonId, TestContext.Current.CancellationToken);

        // Ordering is the controller's job; the service guarantees entries and display names only.
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
        _userService.GetUserInfosAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(),
            Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(new Dictionary<Guid, UserInfo>()));

        await _sut.SaveCampPolygonAsync(campSeasonId, """{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,0]]]}}""", 100.0, userId, cancellationToken: TestContext.Current.CancellationToken);

        var history = await _sut.GetCampPolygonHistoryAsync(campSeasonId, TestContext.Current.CancellationToken);

        history.Should().HaveCount(1);
        history[0].ModifiedByDisplayName.Should().Be(userId.ToString());
    }

    [HumansFact]
    public async Task GetCampPolygonsAsync_IncludesSoundZone_WhenSet()
    {
        var campSeasonId = Guid.NewGuid();
        var campId = Guid.NewGuid();
        var userId = NewUserId();
        const string geoJson = """{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,0]]]}}""";
        await _sut.SaveCampPolygonAsync(campSeasonId, geoJson, 100.0, userId, cancellationToken: TestContext.Current.CancellationToken);

        _campService.GetCampsForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns(new List<CampInfo>
            {
                MakeCampInfo(campId, "test-camp", [MakeCampSeasonInfo(campSeasonId, campId, 2026, "Test Camp", SoundZone.Blue)])
            });

        var polygons = await _sut.GetCampPolygonsAsync(2026, TestContext.Current.CancellationToken);

        polygons.Single().SoundZone.Should().Be(SoundZone.Blue);
    }

    [HumansFact]
    public async Task GetCampPolygonsAsync_SoundZoneIsNull_WhenNotSet()
    {
        var campSeasonId = Guid.NewGuid();
        var campId = Guid.NewGuid();
        var userId = NewUserId();
        const string geoJson = """{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,0]]]}}""";
        await _sut.SaveCampPolygonAsync(campSeasonId, geoJson, 100.0, userId, cancellationToken: TestContext.Current.CancellationToken);

        _campService.GetCampsForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns(new List<CampInfo>
            {
                MakeCampInfo(campId, "test-camp", [MakeCampSeasonInfo(campSeasonId, campId, 2026, "Test Camp")])
            });

        var polygons = await _sut.GetCampPolygonsAsync(2026, TestContext.Current.CancellationToken);

        polygons.Single().SoundZone.Should().BeNull();
    }

    [HumansFact]
    public async Task UpdateOfficialZonesAsync_StoresGeoJson()
    {
        await SeedMapSettingsAsync();
        const string geoJson = """{"type":"FeatureCollection","features":[]}""";

        await _sut.UpdateOfficialZonesAsync(geoJson, Guid.NewGuid(), TestContext.Current.CancellationToken);

        var settings = await CityPlanningDb.CityPlanningSettings.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        settings.OfficialZonesGeoJson.Should().Be(geoJson);
        settings.UpdatedAt.Should().Be(Clock.GetCurrentInstant());
    }

    [HumansFact]
    public async Task DeleteOfficialZonesAsync_SetsNull()
    {
        await SeedMapSettingsAsync();
        var seeded = await CityPlanningDb.CityPlanningSettings.SingleAsync(TestContext.Current.CancellationToken);
        seeded.OfficialZonesGeoJson = """{"type":"FeatureCollection","features":[]}""";
        await CityPlanningDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        CityPlanningDb.Entry(seeded).State = EntityState.Detached;

        await _sut.DeleteOfficialZonesAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        var updated = await CityPlanningDb.CityPlanningSettings.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        updated.OfficialZonesGeoJson.Should().BeNull();
        updated.UpdatedAt.Should().Be(Clock.GetCurrentInstant());
    }

    [HumansFact]
    public async Task UpdateRegistrationInfoAsync_WritesToHighestOpenSeason_NotTheActiveEventYear()
    {
        _campService.GetSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(new CampSettingsInfo(2026, [2026, 2028, 2027]));

        await _sut.UpdateRegistrationInfoAsync("Read this before you register.", NewUserId(), TestContext.Current.CancellationToken);

        var settings = await CityPlanningDb.CityPlanningSettings.AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        settings.Year.Should().Be(2028);
        settings.RegistrationInfo.Should().Be("Read this before you register.");
    }

    [HumansFact]
    public async Task UpdateRegistrationInfoAsync_FallsBackToTheActiveEventYear_WhenNoOpenSeasons()
    {
        SetupCampSettings();

        await _sut.UpdateRegistrationInfoAsync("Blurb", NewUserId(), TestContext.Current.CancellationToken);

        var settings = await CityPlanningDb.CityPlanningSettings.AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        settings.Year.Should().Be(2026);
    }

    [HumansFact]
    public async Task UpdateRegistrationInfoAsync_TrimsInput_AndStoresNullForBlank()
    {
        SetupCampSettings();

        await _sut.UpdateRegistrationInfoAsync("  padded  ", NewUserId(), TestContext.Current.CancellationToken);
        (await _sut.GetRegistrationInfoAsync(TestContext.Current.CancellationToken)).Should().Be("padded");

        await _sut.UpdateRegistrationInfoAsync("   ", NewUserId(), TestContext.Current.CancellationToken);
        (await _sut.GetRegistrationInfoAsync(TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [HumansFact]
    public async Task GetRegistrationInfoAsync_ReadsTheSameYearTheWriteUsed()
    {
        _campService.GetSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(new CampSettingsInfo(2026, [2027]));

        await _sut.UpdateRegistrationInfoAsync("Open-season blurb", NewUserId(), TestContext.Current.CancellationToken);

        // A row keyed to the active event's year must not shadow the open-season one.
        CityPlanningDb.CityPlanningSettings.Add(new CityPlanningSettings
        {
            Year = 2026,
            RegistrationInfo = "Stale public-year blurb",
            UpdatedAt = Clock.GetCurrentInstant()
        });
        await CityPlanningDb.SaveChangesAsync(TestContext.Current.CancellationToken);

        (await _sut.GetRegistrationInfoAsync(TestContext.Current.CancellationToken))
            .Should().Be("Open-season blurb");
    }

    private static CampSeasonInfo MakeCampSeasonInfo(Guid id, Guid campId, int year, string name, SoundZone? soundZone = null) =>
        new(id, campId, string.Empty, year, null, name, string.Empty, string.Empty,
            [], CampSeasonStatus.Pending, YesNoMaybe.No, YesNoMaybe.No, AdultPlayspacePolicy.No,
            0, soundZone, null, null, 0, null, null);

    private static CampInfo MakeCampInfo(Guid id, string slug, IReadOnlyList<CampSeasonInfo> seasons) =>
        new(id, slug, string.Empty, string.Empty, false, 0, seasons);

}
