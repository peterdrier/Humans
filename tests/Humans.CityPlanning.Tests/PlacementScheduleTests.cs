using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Base.Enums;
using Humans.Camps.Contracts;
using Humans.CityPlanning.Contracts;
using Humans.CityPlanning.Data;
using Humans.CityPlanning.Domain;
using Humans.CityPlanning.Services;
using Humans.Settings.Contracts;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using Xunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using NSubstitute;

namespace Humans.CityPlanning.Tests;

/// <summary>
/// The barrio placement window runs on day offsets from the active
/// event's gate opening, in the event's time zone, with the admins' Open/Close as a manual override.
/// The fixture event opens its gate on 1 July 2026 in Europe/Madrid (UTC+2 in summer); the schedule
/// opens on day -30 (1 June), closes after day -10 (21 June) and stays adjustable through day -5 (26 June).
/// </summary>
public sealed class PlacementScheduleTests : CityPlanningTestBase
{
    private const int OpensOffset = -30;
    private const int ClosesOffset = -10;
    private const int ChangeableUntilOffset = -5;
    private const string GeoJson = """{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,0]]]}}""";

    private readonly ICampService _campService = Substitute.For<ICampService>();
    private readonly ITeamService _teamService = Substitute.For<ITeamService>();
    private readonly IAuditLogService _auditLog = Substitute.For<IAuditLogService>();
    private readonly ISettingsService _settingsService = Substitute.For<ISettingsService>();
    private readonly CityPlanningService _sut;

    public PlacementScheduleTests()
        : base(Instant.FromUtc(2026, 6, 10, 10, 0))
    {
        _settingsService.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(BurnFixtures.Burn(year: 2026, timeZoneId: "Europe/Madrid", gateOpeningDate: new LocalDate(2026, 7, 1)));
        _campService.GetSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(new CampSettingsInfo(2026, []));
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, TeamInfo>());
        _sut = new CityPlanningService(
            new CityPlanningRepository(CityPlanningDbFactory), Clock,
            Options.Create(new CityPlanningOptions { CityPlanningTeamSlug = "city-planning" }),
            _campService, _teamService, Substitute.For<IUserService>(), _auditLog, _settingsService);
    }

    private async Task SeedScheduleAsync(
        bool? placementOverride = null,
        int? opens = OpensOffset, int? closes = ClosesOffset, int? changeableUntil = ChangeableUntilOffset,
        int year = 2026)
    {
        CityPlanningDb.CityPlanningSettings.Add(new CityPlanningSettings
        {
            Year = year,
            PlacementOverride = placementOverride,
            PlacementOpensOffset = opens,
            PlacementClosesOffset = closes,
            PlacementChangeableUntilOffset = changeableUntil,
            UpdatedAt = Clock.GetCurrentInstant(),
        });
        await CityPlanningDb.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<BarrioPlacementState> StateAsync() =>
        (await _sut.GetSettingsAsync(TestContext.Current.CancellationToken))!.PlacementState;

    // Each boundary sits on Madrid midnight (22:00 UTC the evening before), so a UTC reading
    // of "today" would land on the wrong side of every one of them.
    [HumansTheory]
    [InlineData(2026, 5, 31, 21, 59, BarrioPlacementState.Closed)]     // 31 May 23:59 Madrid: before opening
    [InlineData(2026, 5, 31, 22, 0, BarrioPlacementState.Open)]        // 1 Jun 00:00 Madrid: opening day
    [InlineData(2026, 6, 21, 21, 59, BarrioPlacementState.Open)]       // 21 Jun 23:59 Madrid: last open day
    [InlineData(2026, 6, 21, 22, 0, BarrioPlacementState.Changeable)]  // 22 Jun 00:00 Madrid: adjust only
    [InlineData(2026, 6, 26, 21, 59, BarrioPlacementState.Changeable)] // 26 Jun 23:59 Madrid: last adjust day
    [InlineData(2026, 6, 26, 22, 0, BarrioPlacementState.Closed)]      // 27 Jun 00:00 Madrid: locked
    public async Task GetSettingsAsync_NoOverride_StateFollowsTheScheduleInTheEventTimeZone(
        int year, int month, int day, int hour, int minute, BarrioPlacementState expected)
    {
        await SeedScheduleAsync();
        Clock.Reset(Instant.FromUtc(year, month, day, hour, minute));

        (await StateAsync()).Should().Be(expected);
    }

    [HumansFact]
    public async Task GetSettingsAsync_ReportsTheScheduleDaysAsCalendarDates()
    {
        await SeedScheduleAsync();

        var settings = (await _sut.GetSettingsAsync(TestContext.Current.CancellationToken))!;

        settings.PlacementOpensOffset.Should().Be(OpensOffset);
        settings.PlacementOpensOn.Should().Be(new LocalDate(2026, 6, 1));
        settings.PlacementClosesOn.Should().Be(new LocalDate(2026, 6, 21));
        settings.PlacementChangeableUntilOn.Should().Be(new LocalDate(2026, 6, 26));
    }

    [HumansFact]
    public async Task GetSettingsAsync_NoChangeableDay_GoesStraightFromOpenToClosed()
    {
        await SeedScheduleAsync(changeableUntil: null);
        Clock.Reset(Instant.FromUtc(2026, 6, 21, 22, 0));

        (await StateAsync()).Should().Be(BarrioPlacementState.Closed);
    }

    [HumansFact]
    public async Task GetSettingsAsync_NoClosingDay_StaysOpenOnceOpened()
    {
        await SeedScheduleAsync(closes: null, changeableUntil: null);
        Clock.Reset(Instant.FromUtc(2026, 8, 1, 12, 0));

        (await StateAsync()).Should().Be(BarrioPlacementState.Open);
    }

    [HumansFact]
    public async Task GetSettingsAsync_NoOpeningDay_IsClosed()
    {
        await SeedScheduleAsync(opens: null);

        (await StateAsync()).Should().Be(BarrioPlacementState.Closed);
    }

    [HumansFact]
    public async Task GetSettingsAsync_YearOtherThanTheActiveEvents_HasNoScheduleAnchor()
    {
        await SeedScheduleAsync(year: 2025);

        var settings = (await _sut.GetSettingsAsync(TestContext.Current.CancellationToken, 2025))!;

        settings.PlacementState.Should().Be(BarrioPlacementState.Closed);
        settings.PlacementOpensOn.Should().BeNull();
    }

    [HumansFact]
    public async Task GetSettingsAsync_ForcedOpen_WinsAfterTheScheduleLocks()
    {
        await SeedScheduleAsync(placementOverride: true);
        Clock.Reset(Instant.FromUtc(2026, 6, 30, 12, 0));

        (await StateAsync()).Should().Be(BarrioPlacementState.Open);
    }

    [HumansFact]
    public async Task GetSettingsAsync_ForcedClosed_WinsInsideTheOpenWindow()
    {
        await SeedScheduleAsync(placementOverride: false);

        (await StateAsync()).Should().Be(BarrioPlacementState.Closed);
    }

    // The migration keeps the old IsPlacementOpen column as the override, so a row an admin had
    // closed or opened by hand reads exactly as before until someone chooses Follow schedule.
    [HumansFact]
    public async Task GetSettingsAsync_ForcedOpenWithNoSchedule_IsOpen_AsTheOldFlagWas()
    {
        await SeedScheduleAsync(placementOverride: true, opens: null, closes: null, changeableUntil: null);

        (await StateAsync()).Should().Be(BarrioPlacementState.Open);
    }

    [HumansFact]
    public async Task ClearPlacementOverrideAsync_HandsPlacementBackToTheSchedule_AndAudits()
    {
        await SeedScheduleAsync(placementOverride: false);
        var adminId = Guid.NewGuid();

        await _sut.ClearPlacementOverrideAsync(adminId, TestContext.Current.CancellationToken);

        var settings = (await _sut.GetSettingsAsync(TestContext.Current.CancellationToken))!;
        settings.PlacementOverride.Should().BeNull();
        settings.PlacementState.Should().Be(BarrioPlacementState.Open);
        await _auditLog.Received(1).LogAsync(
            AuditAction.CityPlanningPlacementOverrideCleared,
            nameof(CityPlanningSettings),
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            adminId,
            Arg.Any<Guid?>(),
            Arg.Any<string?>());
    }

    [HumansFact]
    public async Task CanUserEditAsync_Changeable_LeadMayAdjustAnExistingPolygon()
    {
        var (leadId, seasonId) = SetUpLead();
        await SeedScheduleAsync();
        await _sut.SaveCampPolygonAsync(seasonId, GeoJson, 100, leadId, cancellationToken: TestContext.Current.CancellationToken);
        Clock.Reset(Instant.FromUtc(2026, 6, 24, 12, 0));

        (await _sut.CanUserEditAsync(leadId, seasonId, TestContext.Current.CancellationToken)).Should().BeTrue();
    }

    [HumansFact]
    public async Task CanUserEditAsync_Changeable_LeadMayNotCreateAPolygon()
    {
        var (leadId, seasonId) = SetUpLead();
        await SeedScheduleAsync();
        Clock.Reset(Instant.FromUtc(2026, 6, 24, 12, 0));

        (await _sut.CanUserEditAsync(leadId, seasonId, TestContext.Current.CancellationToken)).Should().BeFalse();
    }

    [HumansFact]
    public async Task CanUserEditAsync_ScheduledOpen_LeadMayCreateAPolygon()
    {
        var (leadId, seasonId) = SetUpLead();
        await SeedScheduleAsync();

        (await _sut.CanUserEditAsync(leadId, seasonId, TestContext.Current.CancellationToken)).Should().BeTrue();
    }

    [HumansFact]
    public async Task CanUserEditAsync_Locked_LeadMayNotTouchAnExistingPolygon()
    {
        var (leadId, seasonId) = SetUpLead();
        await SeedScheduleAsync();
        await _sut.SaveCampPolygonAsync(seasonId, GeoJson, 100, leadId, cancellationToken: TestContext.Current.CancellationToken);
        Clock.Reset(Instant.FromUtc(2026, 6, 28, 12, 0));

        (await _sut.CanUserEditAsync(leadId, seasonId, TestContext.Current.CancellationToken)).Should().BeFalse();
    }

    [HumansFact]
    public async Task CanUserEditAsync_Changeable_SomeoneElsesCampsPolygon_IsRefused()
    {
        var (_, otherSeasonId) = SetUpLead();
        var (leadId, _) = SetUpLead();
        await SeedScheduleAsync();
        await _sut.SaveCampPolygonAsync(otherSeasonId, GeoJson, 100, Guid.NewGuid(), cancellationToken: TestContext.Current.CancellationToken);
        Clock.Reset(Instant.FromUtc(2026, 6, 24, 12, 0));

        (await _sut.CanUserEditAsync(leadId, otherSeasonId, TestContext.Current.CancellationToken)).Should().BeFalse();
    }

    [HumansFact]
    public async Task UpdatePlacementScheduleAsync_PersistsTheOffsets_AndAuditsThem()
    {
        var adminId = Guid.NewGuid();

        var result = await _sut.UpdatePlacementScheduleAsync(OpensOffset, ClosesOffset, ChangeableUntilOffset, adminId, TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        var row = await CityPlanningDb.CityPlanningSettings.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        row.PlacementOpensOffset.Should().Be(OpensOffset);
        row.PlacementClosesOffset.Should().Be(ClosesOffset);
        row.PlacementChangeableUntilOffset.Should().Be(ChangeableUntilOffset);
        row.PlacementOverride.Should().BeNull("editing the schedule leaves the override alone");
        await _auditLog.Received(1).LogAsync(
            AuditAction.CityPlanningPlacementDatesUpdated,
            nameof(CityPlanningSettings),
            row.Id,
            Arg.Is<string>(d => d.Contains("opens -30", StringComparison.Ordinal)
                && d.Contains("closes -10", StringComparison.Ordinal)
                && d.Contains("changeable until -5", StringComparison.Ordinal)),
            adminId,
            Arg.Any<Guid?>(),
            Arg.Any<string?>());
    }

    [HumansFact]
    public async Task UpdatePlacementScheduleAsync_NullsClearTheDays()
    {
        await SeedScheduleAsync();

        var result = await _sut.UpdatePlacementScheduleAsync(null, null, null, Guid.NewGuid(), TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        var settings = (await _sut.GetSettingsAsync(TestContext.Current.CancellationToken))!;
        settings.PlacementOpensOffset.Should().BeNull();
        settings.PlacementClosesOffset.Should().BeNull();
        settings.PlacementChangeableUntilOffset.Should().BeNull();
        settings.PlacementState.Should().Be(BarrioPlacementState.Closed);
    }

    [HumansTheory]
    [InlineData(-10, -30, null, "ClosesBeforeOpens")]
    [InlineData(-30, null, -5, "ChangeableWithoutClose")]
    [InlineData(-30, -10, -20, "ChangeableBeforeCloses")]
    [InlineData(-400, -10, null, "OutOfRange")]
    [InlineData(int.MinValue, null, null, "OutOfRange")]
    [InlineData(null, int.MaxValue, null, "OutOfRange")]
    public async Task UpdatePlacementScheduleAsync_InconsistentDays_AreRefusedWithoutWritingOrAuditing(
        int? opens, int? closes, int? changeableUntil, string errorKey)
    {
        var result = await _sut.UpdatePlacementScheduleAsync(opens, closes, changeableUntil, Guid.NewGuid(), TestContext.Current.CancellationToken);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be(errorKey);
        (await CityPlanningDb.CityPlanningSettings.AsNoTracking().CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
        _auditLog.ReceivedCalls().Should().BeEmpty();
    }

    [HumansFact]
    public void PlacementOverride_IsStoredInTheOldIsPlacementOpenColumn_NowNullable()
    {
        using var context = new CityPlanningDbContextFactory().CreateDbContext([]);
        var property = context.Model.FindEntityType(typeof(CityPlanningSettings))!
            .FindProperty(nameof(CityPlanningSettings.PlacementOverride))!;

        property.GetColumnName().Should().Be("IsPlacementOpen");
        property.IsNullable.Should().BeTrue();
        property.ClrType.Should().Be(typeof(bool?));
    }

    /// <summary>Registers a fresh 2026 camp whose single season <c>leadId</c> leads.</summary>
    private (Guid LeadId, Guid SeasonId) SetUpLead()
    {
        var leadId = Guid.NewGuid();
        var campId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var season = MakeCampSeasonInfo(seasonId, campId, 2026) with { LeadUserIds = [leadId] };
        _campService.GetCampSeasonByIdAsync(seasonId, Arg.Any<CancellationToken>()).Returns(season);
        _camps.Add(new CampInfo(campId, $"camp-{campId:N}", string.Empty, string.Empty, false, 0, [season]));
        _campService.GetCampsForYearAsync(2026, Arg.Any<CancellationToken>()).Returns(_camps.ToList());
        return (leadId, seasonId);
    }

    private readonly List<CampInfo> _camps = [];

    private static CampSeasonInfo MakeCampSeasonInfo(Guid id, Guid campId, int year) =>
        new(id, campId, string.Empty, year, null, "Camp", string.Empty, string.Empty,
            [], CampSeasonStatus.Pending, YesNoMaybe.No, YesNoMaybe.No, AdultPlayspacePolicy.No,
            0, null, null, null, 0, null, null);
}
