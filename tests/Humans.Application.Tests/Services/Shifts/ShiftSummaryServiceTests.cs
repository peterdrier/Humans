using AwesomeAssertions;
using Humans.Application.DTOs.Shifts;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Shifts;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Repositories.Shifts;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;

namespace Humans.Application.Tests.Services.Shifts;

/// <summary>
/// Tests for <see cref="ShiftManagementService.BuildSummaryAsync"/> — the Shift
/// Summary by Camp read. Covers the flat human table, the by-camp pivot with the
/// active-camp roster left-join (absent camps → zero rows), the campless bucket,
/// the global / team-set / single-rota scopes, and rota/slug validation.
/// </summary>
public sealed class ShiftSummaryServiceTests : ServiceTestHarness
{
    private const int PublicYear = 2026;

    private readonly ITeamServiceRead _teamService = Substitute.For<ITeamServiceRead>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly ICampServiceRead _campService = Substitute.For<ICampServiceRead>();
    private readonly ShiftManagementService _service;

    private static readonly Instant TestNow = Instant.FromUtc(2026, 6, 15, 12, 0);

    // Scenario ids.
    private readonly EventSettings _event;
    private readonly Guid _powerId = Guid.NewGuid();
    private readonly Guid _subId = Guid.NewGuid();
    private readonly Guid _waterId = Guid.NewGuid();
    private readonly Guid _rPower = Guid.NewGuid();
    private readonly Guid _rSub = Guid.NewGuid();
    private readonly Guid _rWater = Guid.NewGuid();
    private readonly Guid _userA = Guid.NewGuid();
    private readonly Guid _userB = Guid.NewGuid();
    private readonly Guid _userC = Guid.NewGuid();
    private readonly Guid _fuego = Guid.NewGuid();
    private readonly Guid _mwah = Guid.NewGuid();
    private readonly Guid _tiny = Guid.NewGuid();

    private readonly Dictionary<Guid, string> _names = new();
    private readonly Dictionary<Guid, CampUserInfo> _campByUser = new();

    public ShiftSummaryServiceTests() : base(TestNow)
    {
        _event = new EventSettings
        {
            Id = Guid.NewGuid(),
            EventName = "Test Event",
            GateOpeningDate = new LocalDate(2026, 7, 1),
            TimeZoneId = "UTC",
            IsActive = true
        };

        SeedScenario();

        // Team reads: by-id dictionary (team-set resolution) and by-slug.
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyDictionary<Guid, TeamInfo>>(
                Db.Teams.AsEnumerable().ToDictionary(t => t.Id, ToTeamInfo)));
        _teamService.GetTeamBySlugAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var slug = ci.Arg<string>();
                var team = Db.Teams.AsEnumerable()
                    .FirstOrDefault(t => string.Equals(t.Slug, slug, StringComparison.Ordinal));
                return Task.FromResult(team is null ? null : ToTeamInfo(team));
            });

        // Display names.
        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var ids = ci.Arg<IReadOnlyCollection<Guid>>();
                IReadOnlyDictionary<Guid, UserInfo> dict = ids
                    .Where(_names.ContainsKey)
                    .ToDictionary(id => id, id => UserInfoStubHelpers.MakeUserInfo(id, displayName: _names[id]));
                return new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(dict);
            });

        // Camp reads.
        _campService.GetSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CampSettingsInfo(PublicYear, [], null)));
        _campService.GetCampsForYearAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CampInfo>>(
            [
                MakeCamp(_fuego, "fuego", "Barrio Fuego"),
                MakeCamp(_mwah, "mwah", "Camp Mwah"),
                MakeCamp(_tiny, "tiny", "Tiny Camp"),
            ]));
        _campService.GetCampUserInfoAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(
                _campByUser.GetValueOrDefault(ci.Arg<Guid>(), CampUserInfo.None)));

        var serviceProvider = new ServiceLocatorBuilder()
            .With<ITeamServiceRead>(_teamService)
            .With<IUserServiceRead>(_userService)
            .With<ICampServiceRead>(_campService)
            .Build();

        var repo = new ShiftRepository(DbFactory, Db, Clock);

        _service = new ShiftManagementService(
            repo,
            AuditLog,
            AdminAuthorization,
            serviceProvider,
            Cache,
            Substitute.For<IShiftViewInvalidator>(),
            Clock,
            NullLogger<ShiftManagementService>.Instance);
    }

    [HumansFact]
    public async Task BuildSummaryAsync_Global_FlatTableHasEveryConfirmedHumanWithCamp()
    {
        var summary = await _service.BuildSummaryAsync(_event);

        summary.Should().NotBeNull();
        summary!.Scope.Should().Be(ShiftSummaryScope.Global);

        var byUser = summary.Humans.ToDictionary(h => h.UserId);
        byUser.Keys.Should().BeEquivalentTo([_userA, _userB, _userC]);

        byUser[_userA].Should().BeEquivalentTo(new
        {
            Name = "Ana",
            CampId = (Guid?)_fuego,
            CampName = "Barrio Fuego",
            Hours = 4.0,
            Count = 1
        });
        byUser[_userB].CampId.Should().Be(_fuego);
        byUser[_userB].Hours.Should().Be(8.0);
        // userC is campless → null camp, surfaced in the flat table.
        byUser[_userC].CampId.Should().BeNull();
        byUser[_userC].CampName.Should().BeNull();
        byUser[_userC].Hours.Should().Be(2.0);
    }

    [HumansFact]
    public async Task BuildSummaryAsync_Global_PivotLeftJoinsRosterWithCamplessBucket()
    {
        var summary = await _service.BuildSummaryAsync(_event);

        var byCamp = summary!.Camps.ToDictionary(c => c.CampId ?? Guid.Empty);

        // Fuego: userA (4h) + userB (8h) → 2 people, 12h, 2 signups.
        byCamp[_fuego].People.Should().Be(2);
        byCamp[_fuego].Hours.Should().Be(12.0);
        byCamp[_fuego].Count.Should().Be(2);
        byCamp[_fuego].CampName.Should().Be("Barrio Fuego");

        // Mwah and Tiny are on the roster but nobody signed up → zero rows.
        byCamp[_mwah].People.Should().Be(0);
        byCamp[_mwah].Hours.Should().Be(0.0);
        byCamp[_tiny].People.Should().Be(0);

        // Campless bucket (CampId null) for userC.
        var campless = summary.Camps.Single(c => c.CampId is null);
        campless.People.Should().Be(1);
        campless.Hours.Should().Be(2.0);
        campless.Count.Should().Be(1);
    }

    [HumansFact]
    public async Task BuildSummaryAsync_Global_LinksToEachDepartment()
    {
        var summary = await _service.BuildSummaryAsync(_event);

        // Non-promoted sub-team "Sub" rolls up into "Power"; departments = Power, Water.
        summary!.TeamLinks.Select(t => t.Slug).Should().BeEquivalentTo(["power", "water"]);
        summary.RotaLinks.Should().BeEmpty();
    }

    [HumansFact]
    public async Task BuildSummaryAsync_TeamScope_RestrictsFlatTableToTeamSetButKeepsFullRoster()
    {
        var summary = await _service.BuildSummaryAsync(_event, teamSlug: "power");

        summary.Should().NotBeNull();
        summary!.Scope.Should().Be(ShiftSummaryScope.Team);
        summary.TeamName.Should().Be("Power");
        summary.TeamSlug.Should().Be("power");

        // Team-set = {Power, Sub}. userA (Power) + userC (Sub) only; userB (Water) excluded.
        summary.Humans.Select(h => h.UserId).Should().BeEquivalentTo([_userA, _userC]);

        var byCamp = summary.Camps.ToDictionary(c => c.CampId ?? Guid.Empty);
        // Fuego now only has userA (4h) in this team-set.
        byCamp[_fuego].People.Should().Be(1);
        byCamp[_fuego].Hours.Should().Be(4.0);
        // Roster still left-joined in full: Mwah/Tiny present as zero rows.
        byCamp[_mwah].People.Should().Be(0);
        byCamp[_tiny].People.Should().Be(0);

        // Team page links to each rota in the team-set.
        summary.RotaLinks.Select(r => r.RotaId).Should().BeEquivalentTo([_rPower, _rSub]);
    }

    [HumansFact]
    public async Task BuildSummaryAsync_RotaScope_RestrictsToSingleRota()
    {
        var summary = await _service.BuildSummaryAsync(_event, teamSlug: "power", rotaId: _rPower);

        summary.Should().NotBeNull();
        summary!.Scope.Should().Be(ShiftSummaryScope.Rota);
        summary.RotaId.Should().Be(_rPower);
        summary.RotaName.Should().NotBeNullOrEmpty();

        // Only userA is on rPower.
        summary.Humans.Select(h => h.UserId).Should().BeEquivalentTo([_userA]);
        // No campless human on this rota → no campless bucket row.
        summary.Camps.Should().NotContain(c => c.CampId == null);
    }

    [HumansFact]
    public async Task BuildSummaryAsync_RotaNotInTeamSet_ReturnsNull()
    {
        // rWater belongs to Water, not Power's team-set.
        var summary = await _service.BuildSummaryAsync(_event, teamSlug: "power", rotaId: _rWater);

        summary.Should().BeNull();
    }

    [HumansFact]
    public async Task BuildSummaryAsync_UnknownTeamSlug_ReturnsNull()
    {
        var summary = await _service.BuildSummaryAsync(_event, teamSlug: "does-not-exist");

        summary.Should().BeNull();
    }

    // ── seed + factories ──────────────────────────────────────────────────────

    private void SeedScenario()
    {
        Db.EventSettings.Add(_event);

        var power = SeedTeam(_powerId, "Power");
        var sub = SeedTeam(_subId, "Sub");
        sub.ParentTeamId = _powerId;          // non-promoted sub-team of Power
        sub.IsPromotedToDirectory = false;
        SeedTeam(_waterId, "Water");

        SeedRota(_rPower, _powerId);
        SeedRota(_rSub, _subId);
        SeedRota(_rWater, _waterId);

        var sPower = SeedShift(_rPower, Duration.FromHours(4));
        var sSub = SeedShift(_rSub, Duration.FromHours(2));
        var sWater = SeedShift(_rWater, Duration.FromHours(8));

        Db.ShiftSignups.AddRange(
            MakeSignup(_userA, sPower, SignupStatus.Confirmed),
            MakeSignup(_userB, sWater, SignupStatus.Confirmed),
            MakeSignup(_userC, sSub, SignupStatus.Confirmed));

        Db.SaveChanges();

        _names[_userA] = "Ana";
        _names[_userB] = "Beto";
        _names[_userC] = "Cara";

        _campByUser[_userA] = new CampUserInfo(MakeSeason(_fuego, "fuego", "Barrio Fuego"), []);
        _campByUser[_userB] = new CampUserInfo(MakeSeason(_fuego, "fuego", "Barrio Fuego"), []);
        _campByUser[_userC] = CampUserInfo.None; // campless
    }

    private void SeedRota(Guid rotaId, Guid teamId) => Db.Rotas.Add(new Rota
    {
        Id = rotaId,
        Name = "Rota-" + rotaId.ToString()[..8],
        TeamId = teamId,
        EventSettingsId = _event.Id,
        Policy = SignupPolicy.Public,
        Period = RotaPeriod.Event
    });

    private Guid SeedShift(Guid rotaId, Duration duration)
    {
        var shiftId = Guid.NewGuid();
        Db.Shifts.Add(new Shift
        {
            Id = shiftId,
            RotaId = rotaId,
            DayOffset = 0,
            StartTime = new LocalTime(8, 0),
            Duration = duration,
            MaxVolunteers = 10
        });
        return shiftId;
    }

    private static ShiftSignup MakeSignup(Guid userId, Guid shiftId, SignupStatus status) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        ShiftId = shiftId,
        Status = status,
        CreatedAt = TestNow,
        UpdatedAt = TestNow
    };

    private static TeamInfo ToTeamInfo(Team team) =>
        new(
            team.Id, team.Name, team.Description, team.Slug,
            team.IsActive, team.IsSystemTeam, team.SystemTeamType, team.RequiresApproval,
            team.IsPublicPage, team.IsHidden, team.IsPromotedToDirectory, team.CreatedAt,
            Members: [],
            ParentTeamId: team.ParentTeamId);

    private static CampSeasonInfo MakeSeason(Guid campId, string slug, string name) =>
        new(Guid.NewGuid(), campId, slug, PublicYear, null,
            name, string.Empty, string.Empty, [], CampSeasonStatus.Active,
            YesNoMaybe.No, YesNoMaybe.No, AdultPlayspacePolicy.No,
            0, null, null, null, 0, null, null);

    private static CampInfo MakeCamp(Guid campId, string slug, string name) =>
        new(campId, slug, "e@example.com", "+34 600 000 000", false, 0, [MakeSeason(campId, slug, name)]);
}
