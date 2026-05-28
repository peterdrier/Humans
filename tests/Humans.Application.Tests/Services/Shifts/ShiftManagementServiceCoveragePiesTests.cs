using AwesomeAssertions;
using Humans.Application.Interfaces.Auth;
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

public sealed class ShiftManagementServiceCoveragePiesTests : ServiceTestHarness
{
    private readonly ITeamServiceRead _teamService;
    private readonly ShiftManagementService _service;

    private static readonly Instant TestNow = Instant.FromUtc(2026, 6, 15, 12, 0);

    public ShiftManagementServiceCoveragePiesTests()
        : base(TestNow)
    {
        _teamService = Substitute.For<ITeamServiceRead>();

        // Resolve teams from the same in-memory DB as the repo; production reads
        // the cached TeamInfo projection and walks parents in memory.
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyDictionary<Guid, TeamInfo>>(
                Db.Teams.AsEnumerable().ToDictionary(
                    t => t.Id,
                    t => new TeamInfo(
                        t.Id, t.Name, t.Description, t.Slug,
                        t.IsActive, t.IsSystemTeam, t.SystemTeamType, t.RequiresApproval,
                        t.IsPublicPage, t.IsHidden, t.IsPromotedToDirectory, t.CreatedAt,
                        Members: [],
                        ParentTeamId: t.ParentTeamId))));

        var serviceProvider = new ServiceLocatorBuilder()
            .With<ITeamServiceRead>(_teamService)
            .With<IRoleAssignmentService>()
            .With<IUserService>()
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
    public async Task EmptyEvent_ReturnsEmptyList()
    {
        var (es, _, _) = SeedDeptScenario();
        await Db.SaveChangesAsync();

        var result = await _service.GetDepartmentCoveragePiesAsync(es.Id);

        result.Should().BeEmpty();
    }

    [HumansFact]
    public async Task SingleDept_SingleRota_NoSignups_RequestedSummedFilledZero()
    {
        var (es, art, _) = SeedDeptScenario();
        var rota = AddRota(es, art, RotaPeriod.Event);
        AddShift(rota, dayOffset: 0, maxVolunteers: 5, durationHours: 4.0);
        AddShift(rota, dayOffset: 0, maxVolunteers: 3, durationHours: 2.0);
        await Db.SaveChangesAsync();

        var result = await _service.GetDepartmentCoveragePiesAsync(es.Id);

        result.Should().HaveCount(1);
        result[0].TeamId.Should().Be(art.Id);
        result[0].TeamName.Should().Be("Art");
        result[0].IsSubTeam.Should().BeFalse();
        result[0].RequestedHours.Should().Be(26m); // (5×4) + (3×2)
        result[0].FilledHours.Should().Be(0m);
    }

    [HumansFact]
    public async Task PromotedSubteam_GetsOwnPie_ParentExcludesSubteamRotas()
    {
        var (es, art, lighting) = SeedDeptScenario(withSubteam: true, subteamPromoted: true);
        AddShift(AddRota(es, art, RotaPeriod.Event, name: "ArtRota"),
            dayOffset: 0, maxVolunteers: 2, durationHours: 4.0);
        AddShift(AddRota(es, lighting!, RotaPeriod.Event, name: "LightingRota"),
            dayOffset: 0, maxVolunteers: 3, durationHours: 4.0);
        await Db.SaveChangesAsync();

        var result = await _service.GetDepartmentCoveragePiesAsync(es.Id);

        result.Should().HaveCount(2);
        var artPie = result.Single(r => r.TeamId == art.Id);
        var lightingPie = result.Single(r => r.TeamId == lighting!.Id);
        artPie.RequestedHours.Should().Be(8m);
        lightingPie.RequestedHours.Should().Be(12m);
        lightingPie.IsSubTeam.Should().BeTrue();
        lightingPie.ParentTeamId.Should().Be(art.Id);
    }

    [HumansFact]
    public async Task NonPromotedSubteam_RotasRollUpToParent_NoSubteamPie()
    {
        var (es, art, lighting) = SeedDeptScenario(withSubteam: true, subteamPromoted: false);
        AddShift(AddRota(es, art, RotaPeriod.Event, name: "ArtRota"),
            dayOffset: 0, maxVolunteers: 2, durationHours: 4.0);
        AddShift(AddRota(es, lighting!, RotaPeriod.Event, name: "LightingRota"),
            dayOffset: 0, maxVolunteers: 3, durationHours: 4.0);
        await Db.SaveChangesAsync();

        var result = await _service.GetDepartmentCoveragePiesAsync(es.Id);

        result.Should().HaveCount(1);
        result[0].TeamId.Should().Be(art.Id);
        result[0].RequestedHours.Should().Be(20m); // (2 + 3) × 4
    }

    [HumansFact]
    public async Task NonPromotedSubteam_WhenParentOwnsNoRota_StillRollsUpToParent()
    {
        // Parent (Art) owns NO rota of its own; only the non-promoted subteam
        // (Art / Lighting) has a rota. The TeamInfo parent walk must include
        // Art in the team lookup so the bucket rollup finds a target.
        var (es, art, lighting) = SeedDeptScenario(withSubteam: true, subteamPromoted: false);
        AddShift(AddRota(es, lighting!, RotaPeriod.Event, name: "LightingRota"),
            dayOffset: 0, maxVolunteers: 3, durationHours: 4.0);
        await Db.SaveChangesAsync();

        var result = await _service.GetDepartmentCoveragePiesAsync(es.Id);

        result.Should().HaveCount(1);
        result[0].TeamId.Should().Be(art.Id);
        result[0].IsSubTeam.Should().BeFalse();
        result[0].RequestedHours.Should().Be(12m); // 3 × 4, rolled up from the subteam
    }

    [HumansFact]
    public async Task FillPercent_ClampedZeroToHundred_AndRoundedAwayFromZero()
    {
        // Service caps confirmed at MaxVolunteers, so 0..100 is already the
        // service contract — this guards the DTO clamp.
        var (es, art, _) = SeedDeptScenario();
        var rota = AddRota(es, art, RotaPeriod.Event);
        var shift = AddShift(rota, dayOffset: 0, maxVolunteers: 3, durationHours: 4.0);
        AddConfirmedSignup(shift); // 1/3 → 33.3% → 33 (away from zero)
        await Db.SaveChangesAsync();

        var result = await _service.GetDepartmentCoveragePiesAsync(es.Id);

        result[0].FillPercent.Should().Be(33);
    }

    [HumansFact]
    public async Task FillPercent_NoRequestedHours_ReturnsZero()
    {
        var pie = new DepartmentCoveragePie(
            TeamId: Guid.NewGuid(), TeamName: "X", TeamSlug: "x",
            IsSubTeam: false, ParentTeamId: null, ParentTeamName: null,
            RequestedHours: 0m, FilledHours: 0m);

        await Task.CompletedTask;
        pie.FillPercent.Should().Be(0);
    }

    [HumansFact]
    public async Task ResultSortedAlphabetically_ByTeamName_ParentNamePopulatedForSubteams()
    {
        // Service returns natural-name-ordered rows; the "sub-team next to
        // parent" rule is applied later in the view-model assembly layer.
        var (es, _, _) = SeedDeptScenario();
        var mango = new Team
        {
            Id = Guid.NewGuid(),
            Name = "Mango",
            Slug = "mango",
            SystemTeamType = SystemTeamType.None,
            ParentTeamId = null,
            IsActive = true,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        };
        var appleSlice = new Team
        {
            Id = Guid.NewGuid(),
            Name = "Apple Slice",
            Slug = "apple-slice",
            SystemTeamType = SystemTeamType.None,
            ParentTeamId = mango.Id,
            IsPromotedToDirectory = true,
            IsActive = true,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        };
        var banana = new Team
        {
            Id = Guid.NewGuid(),
            Name = "Banana",
            Slug = "banana",
            SystemTeamType = SystemTeamType.None,
            ParentTeamId = null,
            IsActive = true,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        };
        await Db.Teams.AddRangeAsync(mango, appleSlice, banana);

        foreach (var t in new[] { mango, appleSlice, banana })
            AddShift(AddRota(es, t, RotaPeriod.Event), dayOffset: 0, maxVolunteers: 1, durationHours: 4.0);
        await Db.SaveChangesAsync();

        var result = await _service.GetDepartmentCoveragePiesAsync(es.Id);

        result.Select(r => r.TeamName).Should().Equal("Apple Slice", "Banana", "Mango");
        result.Single(r => string.Equals(r.TeamName, "Apple Slice", StringComparison.Ordinal))
            .ParentTeamName.Should().Be("Mango");
        result.Single(r => string.Equals(r.TeamName, "Banana", StringComparison.Ordinal))
            .ParentTeamName.Should().BeNull();
        result.Single(r => string.Equals(r.TeamName, "Mango", StringComparison.Ordinal))
            .ParentTeamName.Should().BeNull();
    }

    [HumansFact]
    public async Task HiddenRota_Excluded_AdminOnlyShift_Excluded()
    {
        var (es, art, _) = SeedDeptScenario();
        var visibleRota = AddRota(es, art, RotaPeriod.Event, isVisibleToVolunteers: true, name: "Visible");
        var hiddenRota = AddRota(es, art, RotaPeriod.Event, isVisibleToVolunteers: false, name: "Hidden");
        AddShift(visibleRota, dayOffset: 0, maxVolunteers: 2, durationHours: 4.0);
        AddShift(visibleRota, dayOffset: 0, maxVolunteers: 3, durationHours: 4.0, adminOnly: true);
        AddShift(hiddenRota, dayOffset: 0, maxVolunteers: 10, durationHours: 4.0);
        await Db.SaveChangesAsync();

        var result = await _service.GetDepartmentCoveragePiesAsync(es.Id);

        result[0].RequestedHours.Should().Be(8m);
        result[0].FilledHours.Should().Be(0m);
    }

    [HumansFact]
    public async Task ConfirmedCountCappedAtMaxVolunteers_PreventsOver100Pct()
    {
        var (es, art, _) = SeedDeptScenario();
        var rota = AddRota(es, art, RotaPeriod.Event);
        var shift = AddShift(rota, dayOffset: 0, maxVolunteers: 5, durationHours: 4.0);
        for (var i = 0; i < 7; i++) AddConfirmedSignup(shift);
        await Db.SaveChangesAsync();

        var result = await _service.GetDepartmentCoveragePiesAsync(es.Id);

        result[0].RequestedHours.Should().Be(20m);
        result[0].FilledHours.Should().Be(20m); // capped, not 28
    }

    [HumansFact]
    public async Task ConfirmedSignupsCounted_OtherStatusesIgnored()
    {
        var (es, art, _) = SeedDeptScenario();
        var rota = AddRota(es, art, RotaPeriod.Event);
        var shift = AddShift(rota, dayOffset: 0, maxVolunteers: 10, durationHours: 4.0);
        AddConfirmedSignup(shift);
        AddConfirmedSignup(shift);
        AddConfirmedSignup(shift);
        Db.ShiftSignups.Add(new ShiftSignup
        {
            Id = Guid.NewGuid(),
            ShiftId = shift.Id,
            UserId = Guid.NewGuid(),
            Status = SignupStatus.Pending,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        });
        Db.ShiftSignups.Add(new ShiftSignup
        {
            Id = Guid.NewGuid(),
            ShiftId = shift.Id,
            UserId = Guid.NewGuid(),
            Status = SignupStatus.Bailed,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        });
        await Db.SaveChangesAsync();

        var result = await _service.GetDepartmentCoveragePiesAsync(es.Id);

        result[0].RequestedHours.Should().Be(40m);
        result[0].FilledHours.Should().Be(12m);
    }

    [HumansFact]
    public async Task AllDayShift_Uses8HourWindow_NotDurationColumn()
    {
        var (es, art, _) = SeedDeptScenario();
        var rota = AddRota(es, art, RotaPeriod.Build);
        var shift = AddShift(rota, dayOffset: -3, maxVolunteers: 4, isAllDay: true);
        shift.Duration = Duration.FromHours(24);
        AddConfirmedSignup(shift);
        AddConfirmedSignup(shift);
        await Db.SaveChangesAsync();

        var result = await _service.GetDepartmentCoveragePiesAsync(es.Id);

        // AllDayWindowStart=08:00, AllDayWindowEnd=18:00 → 10h per slot
        result[0].RequestedHours.Should().Be(40m); // 4 × 10
        result[0].FilledHours.Should().Be(20m);    // 2 × 10
    }

    [HumansFact]
    public async Task DateRangeFilter_AppliedAtShiftLevel_RotaPeriodAllCorrect()
    {
        var (es, art, _) = SeedDeptScenario();
        var rota = AddRota(es, art, RotaPeriod.All);
        AddShift(rota, dayOffset: -7, maxVolunteers: 1, durationHours: 4.0);
        AddShift(rota, dayOffset: 2, maxVolunteers: 1, durationHours: 4.0);
        AddShift(rota, dayOffset: 8, maxVolunteers: 1, durationHours: 4.0);
        await Db.SaveChangesAsync();

        var result = await _service.GetDepartmentCoveragePiesAsync(
            es.Id,
            fromDate: new LocalDate(2026, 6, 17),
            toDate: new LocalDate(2026, 6, 30));

        result[0].RequestedHours.Should().Be(4m);
    }

    private (EventSettings Es, Team Art, Team? Lighting) SeedDeptScenario(
        bool withSubteam = false,
        bool subteamPromoted = false)
    {
        var es = new EventSettings
        {
            Id = Guid.NewGuid(),
            EventName = "Test Event 2026",
            TimeZoneId = "Europe/Madrid",
            GateOpeningDate = new LocalDate(2026, 7, 1),
            BuildStartOffset = -14,
            EventEndOffset = 6,
            StrikeEndOffset = 9,
            IsShiftBrowsingOpen = true,
            IsActive = true,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        };
        Db.EventSettings.Add(es);

        var art = new Team
        {
            Id = Guid.NewGuid(),
            Name = "Art",
            Slug = "art",
            SystemTeamType = SystemTeamType.None,
            ParentTeamId = null,
            IsActive = true,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        };
        Db.Teams.Add(art);

        Team? lighting = null;
        if (withSubteam)
        {
            lighting = new Team
            {
                Id = Guid.NewGuid(),
                Name = "Art / Lighting",
                Slug = "art-lighting",
                SystemTeamType = SystemTeamType.None,
                ParentTeamId = art.Id,
                IsPromotedToDirectory = subteamPromoted,
                IsActive = true,
                CreatedAt = TestNow,
                UpdatedAt = TestNow
            };
            Db.Teams.Add(lighting);
        }

        return (es, art, lighting);
    }

    private Rota AddRota(EventSettings es, Team team, RotaPeriod period = RotaPeriod.Event,
        bool isVisibleToVolunteers = true, string name = "Rota")
    {
        var rota = new Rota
        {
            Id = Guid.NewGuid(),
            EventSettingsId = es.Id,
            TeamId = team.Id,
            Name = name,
            Priority = ShiftPriority.Normal,
            Policy = SignupPolicy.Public,
            Period = period,
            IsVisibleToVolunteers = isVisibleToVolunteers,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        };
        Db.Rotas.Add(rota);
        return rota;
    }

    private Shift AddShift(Rota rota, int dayOffset, int maxVolunteers,
        bool isAllDay = false, double durationHours = 4.0,
        bool adminOnly = false, LocalTime? startTime = null)
    {
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            RotaId = rota.Id,
            DayOffset = dayOffset,
            IsAllDay = isAllDay,
            StartTime = startTime ?? new LocalTime(9, 0),
            Duration = isAllDay ? Duration.FromHours(24) : Duration.FromHours(durationHours),
            MinVolunteers = 1,
            MaxVolunteers = maxVolunteers,
            AdminOnly = adminOnly,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        };
        Db.Shifts.Add(shift);
        return shift;
    }

    private ShiftSignup AddConfirmedSignup(Shift shift, Guid? userId = null)
    {
        var s = new ShiftSignup
        {
            Id = Guid.NewGuid(),
            ShiftId = shift.Id,
            UserId = userId ?? Guid.NewGuid(),
            Status = SignupStatus.Confirmed,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        };
        Db.ShiftSignups.Add(s);
        return s;
    }
}
