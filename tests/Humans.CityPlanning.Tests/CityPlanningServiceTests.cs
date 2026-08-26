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
using Humans.Users.Contracts;

namespace Humans.CityPlanning.Tests;

public sealed class CityPlanningServiceTests : CityPlanningTestBase
{
    private readonly ICampServiceRead _campService;
    private readonly ITeamServiceRead _teamService;
    private readonly IUserServiceRead _userService;
    private readonly IAuditLogService _auditLog;
    private readonly CityPlanningService _sut;
    private readonly CityPlanningOptions _options = new() { CityPlanningTeamSlug = "city-planning" };

    public CityPlanningServiceTests()
        : base(Instant.FromUtc(2026, 3, 15, 12, 0, 0))
    {
        _campService = Substitute.For<ICampServiceRead>();
        _teamService = Substitute.For<ITeamServiceRead>();
        _userService = Substitute.For<IUserServiceRead>();
        _auditLog = Substitute.For<IAuditLogService>();
        var repo = new CityPlanningRepository(CityPlanningDbFactory);
        _sut = new CityPlanningService(
            repo, Clock, Options.Create(_options),
            _campService, _teamService, _userService, _auditLog);
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
        CityPlanningDb.CityPlanningSettings.Add(settings);
        await CityPlanningDb.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
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

    // --- Tests ---

    [HumansFact]
    public async Task SaveCampPolygonAsync_FirstSave_CreatesBothPolygonAndHistory()
    {
        var campSeasonId = Guid.NewGuid();
        var userId = NewUserId();
        const string geoJson = """{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[]]}}""";

        await _sut.SaveCampPolygonAsync(campSeasonId, geoJson, 500.0, userId, cancellationToken: Xunit.TestContext.Current.CancellationToken);

        var polygon = await CityPlanningDb.CampPolygons.AsNoTracking().SingleAsync(p => p.CampSeasonId == campSeasonId, Xunit.TestContext.Current.CancellationToken);
        var history = await CityPlanningDb.CampPolygonHistories.AsNoTracking().SingleAsync(h => h.CampSeasonId == campSeasonId, Xunit.TestContext.Current.CancellationToken);

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

        var polygonCount = await CityPlanningDb.CampPolygons.AsNoTracking().CountAsync(p => p.CampSeasonId == campSeasonId, Xunit.TestContext.Current.CancellationToken);
        var historyCount = await CityPlanningDb.CampPolygonHistories.AsNoTracking().CountAsync(h => h.CampSeasonId == campSeasonId, Xunit.TestContext.Current.CancellationToken);
        var polygon = await CityPlanningDb.CampPolygons.AsNoTracking().SingleAsync(p => p.CampSeasonId == campSeasonId, Xunit.TestContext.Current.CancellationToken);

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
        var settings = await CityPlanningDb.CityPlanningSettings.AsNoTracking().SingleAsync(Xunit.TestContext.Current.CancellationToken);
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
        var settings = await CityPlanningDb.CityPlanningSettings.AsNoTracking().SingleAsync(Xunit.TestContext.Current.CancellationToken);
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
    public async Task UpdateLimitZoneFromUploadAsync_NoFile_ReturnsMissingFile()
    {
        var result = await _sut.UpdateLimitZoneFromUploadAsync(null, NewUserId(), Xunit.TestContext.Current.CancellationToken);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("MissingFile");
    }

    [HumansFact]
    public async Task UpdateLimitZoneFromUploadAsync_EmptyFile_ReturnsMissingFile()
    {
        var result = await _sut.UpdateLimitZoneFromUploadAsync(
            CreateUpload(""), NewUserId(), Xunit.TestContext.Current.CancellationToken);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("MissingFile");
    }

    [HumansFact]
    public async Task UpdateLimitZoneFromUploadAsync_OverTenMegabytes_ReturnsFileTooLarge()
    {
        var result = await _sut.UpdateLimitZoneFromUploadAsync(
            CreateOversizedUpload(), NewUserId(), Xunit.TestContext.Current.CancellationToken);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("FileTooLarge");
    }

    [HumansFact]
    public async Task UpdateOfficialZonesFromUploadAsync_NoFile_ReturnsMissingFile()
    {
        var result = await _sut.UpdateOfficialZonesFromUploadAsync(null, NewUserId(), Xunit.TestContext.Current.CancellationToken);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("MissingFile");
    }

    [HumansFact]
    public async Task UpdateOfficialZonesFromUploadAsync_OverTenMegabytes_ReturnsFileTooLarge()
    {
        var result = await _sut.UpdateOfficialZonesFromUploadAsync(
            CreateOversizedUpload(), NewUserId(), Xunit.TestContext.Current.CancellationToken);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("FileTooLarge");
    }

    [HumansFact]
    public async Task UpdateOfficialZonesFromUploadAsync_ValidFile_PersistsOfficialZones()
    {
        await SeedMapSettingsAsync();
        var file = CreateUpload("""{"type":"FeatureCollection","features":[]}""");

        var result = await _sut.UpdateOfficialZonesFromUploadAsync(file, NewUserId(), Xunit.TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        var settings = await CityPlanningDb.CityPlanningSettings.AsNoTracking().SingleAsync(Xunit.TestContext.Current.CancellationToken);
        settings.OfficialZonesGeoJson.Should().Be("""{"type":"FeatureCollection","features":[]}""");
    }

    [HumansFact]
    public async Task RestoreCampPolygonVersionAsync_RestoresGeoJsonWithNote()
    {
        var campSeasonId = Guid.NewGuid();
        var userId = NewUserId();
        const string originalGeoJson = """{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,0]]]}}""";

        await _sut.SaveCampPolygonAsync(campSeasonId, originalGeoJson, 100.0, userId, cancellationToken: Xunit.TestContext.Current.CancellationToken);
        var originalHistory = await CityPlanningDb.CampPolygonHistories.AsNoTracking()
            .SingleAsync(h => h.CampSeasonId == campSeasonId, Xunit.TestContext.Current.CancellationToken);
        Clock.Advance(Duration.FromSeconds(1));
        await _sut.SaveCampPolygonAsync(campSeasonId, """{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[[0,0],[5,0],[5,5],[0,0]]]}}""", 999.0, userId, cancellationToken: Xunit.TestContext.Current.CancellationToken);
        Clock.Advance(Duration.FromSeconds(1));

        await _sut.RestoreCampPolygonVersionAsync(campSeasonId, originalHistory.Id, userId, Xunit.TestContext.Current.CancellationToken);

        var polygon = await CityPlanningDb.CampPolygons.AsNoTracking().SingleAsync(p => p.CampSeasonId == campSeasonId, Xunit.TestContext.Current.CancellationToken);
        var latestHistory = await CityPlanningDb.CampPolygonHistories.AsNoTracking()
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

    // --- Audit: the settings row records when a phase or zone changed, never who. These
    // writes are organisers acting on the members' behalf, so the actor lives in the audit log.

    [HumansFact]
    public async Task OpenPlacementAsync_WritesAuditEntry_NamingTheActor()
    {
        var settings = await SeedMapSettingsAsync();
        var userId = NewUserId();

        await _sut.OpenPlacementAsync(userId, Xunit.TestContext.Current.CancellationToken);

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

        await _sut.ClosePlacementAsync(userId, Xunit.TestContext.Current.CancellationToken);

        await _auditLog.Received(1).LogAsync(
            AuditAction.CityPlanningPlacementClosed,
            Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(), userId,
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task ContainerPlacementToggles_WriteTheirOwnAuditActions()
    {
        await SeedMapSettingsAsync();
        var userId = NewUserId();

        await _sut.OpenContainerPlacementAsync(userId, Xunit.TestContext.Current.CancellationToken);
        await _sut.CloseContainerPlacementAsync(userId, Xunit.TestContext.Current.CancellationToken);

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

        await _sut.UpdateOfficialZonesFromUploadAsync(file, userId, Xunit.TestContext.Current.CancellationToken);
        await _sut.DeleteLimitZoneAsync(userId, Xunit.TestContext.Current.CancellationToken);

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
            CreateUpload("{not-json"), NewUserId(), Xunit.TestContext.Current.CancellationToken);

        result.Success.Should().BeFalse();
        await _auditLog.DidNotReceive().LogAsync(
            AuditAction.CityPlanningLimitZoneUpdated,
            Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    // IAuditLogService takes no cancellation token by design. Before the fix the settings row
    // was re-read after the save to get its id, so a request aborted during the write committed
    // the change and never reached LogAsync — the actor record lost exactly when someone hit
    // stop mid-click. The id is now resolved before the write.
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
            _campService, _teamService, _userService, _auditLog);

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
            _campService, _teamService, _userService, _auditLog);

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

    // The slug is normalized on both sides. Before that it was lower-cased on the configured
    // side only and compared Ordinal, so a stored slug carrying any uppercase never matched
    // and every city-planning team member silently lost their map-admin exemption.
    [HumansFact]
    public async Task IsCityPlanningTeamMemberAsync_StoredSlugHasUppercase_StillMatches()
    {
        var userId = NewUserId();
        SetupCityPlanningTeam(Guid.NewGuid(), memberUserId: userId, slug: "City-Planning");

        var result = await _sut.IsCityPlanningTeamMemberAsync(userId, Xunit.TestContext.Current.CancellationToken);
        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task IsCityPlanningTeamMemberAsync_ConfiguredSlugHasUppercase_StillMatches()
    {
        var userId = NewUserId();
        _options.CityPlanningTeamSlug = "City-Planning";
        SetupCityPlanningTeam(Guid.NewGuid(), memberUserId: userId);

        var result = await _sut.IsCityPlanningTeamMemberAsync(userId, Xunit.TestContext.Current.CancellationToken);
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

        var result = await _sut.IsCityPlanningTeamMemberAsync(userId, Xunit.TestContext.Current.CancellationToken);
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

        _teamService.GetTeamBySlugAsync("city-planning", Arg.Any<CancellationToken>())
            .Returns((TeamInfo?)null);

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

        _teamService.GetTeamBySlugAsync("city-planning", Arg.Any<CancellationToken>())
            .Returns((TeamInfo?)null);

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
        (await CityPlanningDb.CityPlanningSettings.AsNoTracking().CountAsync(Xunit.TestContext.Current.CancellationToken)).Should().Be(1);
    }

    [HumansFact]
    public async Task GetSettingsAsync_ReturnsExistingRow()
    {
        var existing = await SeedMapSettingsAsync(year: 2026, placementOpen: true);

        var result = await _sut.GetSettingsAsync(Xunit.TestContext.Current.CancellationToken);

        result.Id.Should().Be(existing.Id);
        result.IsPlacementOpen.Should().BeTrue();
        (await CityPlanningDb.CityPlanningSettings.AsNoTracking().CountAsync(Xunit.TestContext.Current.CancellationToken)).Should().Be(1);
    }

    [HumansFact]
    public async Task OpenPlacementAsync_SetsIsPlacementOpenTrue()
    {
        await SeedMapSettingsAsync(placementOpen: false);
        var adminId = NewUserId();

        await _sut.OpenPlacementAsync(adminId, Xunit.TestContext.Current.CancellationToken);

        var settings = await CityPlanningDb.CityPlanningSettings.AsNoTracking().SingleAsync(Xunit.TestContext.Current.CancellationToken);
        settings.IsPlacementOpen.Should().BeTrue();
        settings.OpenedAt.Should().Be(Clock.GetCurrentInstant());
    }

    [HumansFact]
    public async Task ClosePlacementAsync_SetsIsPlacementOpenFalse()
    {
        await SeedMapSettingsAsync(placementOpen: true);
        var adminId = NewUserId();

        await _sut.ClosePlacementAsync(adminId, Xunit.TestContext.Current.CancellationToken);

        var settings = await CityPlanningDb.CityPlanningSettings.AsNoTracking().SingleAsync(Xunit.TestContext.Current.CancellationToken);
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
        _userService.GetUserInfosAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(userId)),
            Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(
                // UserInfo.Create inline rather than linking Base's UserInfoStubHelpers: this
                // suite needs one of its members, and the helper is built around UsersDbContext
                // (design §15 step 8).
                new Dictionary<Guid, UserInfo>
                {
                    [userId] = UserInfo.Create(testUser, [], [], [], profile: null, []),
                }));

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
    public async Task UpdatePlacementDatesAsync_ClearsDates_WhenNull()
    {
        await SeedMapSettingsAsync();
        var seeded = await CityPlanningDb.CityPlanningSettings.SingleAsync(Xunit.TestContext.Current.CancellationToken);
        seeded.PlacementOpensAt = new LocalDateTime(2026, 4, 10, 18, 0);
        seeded.PlacementClosesAt = new LocalDateTime(2026, 4, 20, 23, 59);
        await CityPlanningDb.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        CityPlanningDb.Entry(seeded).State = EntityState.Detached;

        var result = await _sut.UpdatePlacementDatesAsync(null, null, Xunit.TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        var updated = await CityPlanningDb.CityPlanningSettings.AsNoTracking().SingleAsync(Xunit.TestContext.Current.CancellationToken);
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

        var settings = await CityPlanningDb.CityPlanningSettings.AsNoTracking().SingleAsync(Xunit.TestContext.Current.CancellationToken);
        settings.OfficialZonesGeoJson.Should().Be(geoJson);
        settings.UpdatedAt.Should().Be(Clock.GetCurrentInstant());
    }

    [HumansFact]
    public async Task DeleteOfficialZonesAsync_SetsNull()
    {
        await SeedMapSettingsAsync();
        var seeded = await CityPlanningDb.CityPlanningSettings.SingleAsync(Xunit.TestContext.Current.CancellationToken);
        seeded.OfficialZonesGeoJson = """{"type":"FeatureCollection","features":[]}""";
        await CityPlanningDb.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        CityPlanningDb.Entry(seeded).State = EntityState.Detached;

        await _sut.DeleteOfficialZonesAsync(Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        var updated = await CityPlanningDb.CityPlanningSettings.AsNoTracking().SingleAsync(Xunit.TestContext.Current.CancellationToken);
        updated.OfficialZonesGeoJson.Should().BeNull();
        updated.UpdatedAt.Should().Be(Clock.GetCurrentInstant());
    }

    // --- RegistrationInfo: keyed to the highest open season, not PublicYear ---

    [HumansFact]
    public async Task UpdateRegistrationInfoAsync_WritesToHighestOpenSeason_NotPublicYear()
    {
        _campService.GetSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(new CampSettingsInfo(2026, [2026, 2028, 2027], null));

        await _sut.UpdateRegistrationInfoAsync("Read this before you register.", Xunit.TestContext.Current.CancellationToken);

        var settings = await CityPlanningDb.CityPlanningSettings.AsNoTracking()
            .SingleAsync(Xunit.TestContext.Current.CancellationToken);
        settings.Year.Should().Be(2028);
        settings.RegistrationInfo.Should().Be("Read this before you register.");
    }

    [HumansFact]
    public async Task UpdateRegistrationInfoAsync_FallsBackToPublicYear_WhenNoOpenSeasons()
    {
        SetupCampSettings(2026);

        await _sut.UpdateRegistrationInfoAsync("Blurb", Xunit.TestContext.Current.CancellationToken);

        var settings = await CityPlanningDb.CityPlanningSettings.AsNoTracking()
            .SingleAsync(Xunit.TestContext.Current.CancellationToken);
        settings.Year.Should().Be(2026);
    }

    [HumansFact]
    public async Task UpdateRegistrationInfoAsync_TrimsInput_AndStoresNullForBlank()
    {
        SetupCampSettings(2026);

        await _sut.UpdateRegistrationInfoAsync("  padded  ", Xunit.TestContext.Current.CancellationToken);
        (await _sut.GetRegistrationInfoAsync(Xunit.TestContext.Current.CancellationToken)).Should().Be("padded");

        await _sut.UpdateRegistrationInfoAsync("   ", Xunit.TestContext.Current.CancellationToken);
        (await _sut.GetRegistrationInfoAsync(Xunit.TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [HumansFact]
    public async Task GetRegistrationInfoAsync_ReadsTheSameYearTheWriteUsed()
    {
        _campService.GetSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(new CampSettingsInfo(2026, [2027], null));

        await _sut.UpdateRegistrationInfoAsync("Open-season blurb", Xunit.TestContext.Current.CancellationToken);

        // A row keyed to PublicYear must not shadow the open-season one.
        CityPlanningDb.CityPlanningSettings.Add(new CityPlanningSettings
        {
            Year = 2026,
            RegistrationInfo = "Stale public-year blurb",
            UpdatedAt = Clock.GetCurrentInstant()
        });
        await CityPlanningDb.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        (await _sut.GetRegistrationInfoAsync(Xunit.TestContext.Current.CancellationToken))
            .Should().Be("Open-season blurb");
    }

    private static CampSeasonInfo MakeCampSeasonInfo(Guid id, Guid campId, int year, string name, SoundZone? soundZone = null) =>
        new(id, campId, string.Empty, year, null, name, string.Empty, string.Empty,
            [], CampSeasonStatus.Pending, YesNoMaybe.No, YesNoMaybe.No, AdultPlayspacePolicy.No,
            0, soundZone, null, null, 0, null, null);

    private static CampInfo MakeCampInfo(Guid id, string slug, IReadOnlyList<CampSeasonInfo> seasons) =>
        new(id, slug, string.Empty, string.Empty, false, 0, seasons);

}
