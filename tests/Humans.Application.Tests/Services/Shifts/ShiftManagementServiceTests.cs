using AwesomeAssertions;
using Humans.Application.Enums;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Shifts;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Repositories.Shifts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;

namespace Humans.Application.Tests.Services.Shifts;

public sealed class ShiftManagementServiceTests : ServiceTestHarness
{
    private readonly ITeamService _teamService;
    private readonly IUserService _userService;
    private readonly IRoleAssignmentService _roleAssignmentService;
    private readonly ShiftManagementService _service;

    private static readonly Instant TestNow = Instant.FromUtc(2026, 6, 15, 12, 0);

    public ShiftManagementServiceTests()
        : base(TestNow)
    {
        _teamService = Substitute.For<ITeamService>();
        _userService = Substitute.For<IUserService>();
        _roleAssignmentService = Substitute.For<IRoleAssignmentService>();

        // Default: users looked up by id resolve to the entities seeded in Db
        // so the cross-section signup stitching in GetBrowseShiftsAsync returns the
        // correct DisplayName.
        _userService.GetByIdsAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var ids = ci.Arg<IReadOnlyCollection<Guid>>();
                return Task.FromResult<IReadOnlyDictionary<Guid, User>>(
                    Db.Users
                        .Where(u => ids.Contains(u.Id))
                        .AsEnumerable()
                        .ToDictionary(u => u.Id));
            });
        _userService.StubGetUserInfosFromContext(Db);

        _teamService.GetByIdsWithParentsAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var ids = ci.Arg<IReadOnlyCollection<Guid>>();
                return Task.FromResult<IReadOnlyDictionary<Guid, Team>>(
                    Db.Teams
                        .Where(t => ids.Contains(t.Id))
                        .AsEnumerable()
                        .ToDictionary(t => t.Id));
            });

        _teamService.GetAllTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyList<Team>>(Db.Teams.AsEnumerable().ToList()));

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
            .With(_teamService)
            .With(_userService)
            .With<IUserServiceRead>(_userService)
            .With(_roleAssignmentService)
            .Build();

        var repo = new ShiftManagementRepository(DbFactory);

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

    // ============================================================
    // CreateBuildStrikeShiftsAsync
    // ============================================================

    [HumansFact]
    public async Task DeleteEventAsync_EvictsDashboardCaches()
    {
        var eventId = Guid.NewGuid();
        var repo = Substitute.For<IShiftManagementRepository>();
        repo.DeleteEventCascadeAsync(eventId, Arg.Any<CancellationToken>())
            .Returns(1);
        using var cache = new MemoryCache(new MemoryCacheOptions());

        var periods = new ShiftPeriod?[] { null }.Concat(Enum.GetValues<ShiftPeriod>().Cast<ShiftPeriod?>()).ToList();
        foreach (var period in periods)
        {
            cache.Set(ShiftManagementService.OverviewCacheKey(eventId, period), new object());
            cache.Set(ShiftManagementService.CoordinatorActivityCacheKey(eventId, period), new object());
            foreach (var window in Enum.GetValues<TrendWindow>())
                cache.Set(ShiftManagementService.TrendsCacheKey(eventId, window, period), new object());
        }

        var service = new ShiftManagementService(
            repo,
            AuditLog,
            AdminAuthorization,
            new ServiceLocatorBuilder().Build(),
            cache,
            Substitute.For<IShiftViewInvalidator>(),
            Clock,
            NullLogger<ShiftManagementService>.Instance);

        var deleted = await service.DeleteEventAsync(eventId);

        deleted.Should().Be(1);
        await AdminAuthorization.Received(1)
            .RequireCurrentUserIsAdminAsync(Arg.Any<CancellationToken>());
        foreach (var period in periods)
        {
            cache.TryGetValue(ShiftManagementService.OverviewCacheKey(eventId, period), out _).Should().BeFalse();
            cache.TryGetValue(ShiftManagementService.CoordinatorActivityCacheKey(eventId, period), out _).Should().BeFalse();
            foreach (var window in Enum.GetValues<TrendWindow>())
                cache.TryGetValue(ShiftManagementService.TrendsCacheKey(eventId, window, period), out _).Should().BeFalse();
        }
    }

    [HumansFact]
    public async Task CreateBuildStrikeShifts_CreatesOneAllDayShiftPerDay()
    {
        // Arrange: rota with Period=Build, staffing grid for days -3 to -1
        var (es, rota) = SeedRotaScenario(RotaPeriod.Build);
        await Db.SaveChangesAsync();

        var staffing = new List<DayStaffingInput>
        {
            new(-3, 2, 5),
            new(-2, 2, 5),
            new(-1, 2, 5)
        };

        // Act
        var result = await _service.CreateBuildStrikeShiftsAsync(new ConfigureBuildStrikeStaffingInput(
            rota.Id,
            rota.TeamId,
            staffing));

        result.Succeeded.Should().BeTrue();
        result.CreatedCount.Should().Be(3);

        // Assert: 3 shifts created, all IsAllDay=true, correct DayOffsets.
        // StartTime/Duration are stored as the midnight/24h sentinel (don't-care for IsAllDay rows).
        var shifts = await Db.Shifts.Where(s => s.RotaId == rota.Id).ToListAsync();
        shifts.Should().HaveCount(3);
        shifts.Should().AllSatisfy(s =>
        {
            s.IsAllDay.Should().BeTrue();
            s.StartTime.Should().Be(LocalTime.Midnight);
            s.Duration.Should().Be(Duration.FromHours(24));
        });
        shifts.Select(s => s.DayOffset).Should().BeEquivalentTo([-3, -2, -1]);
    }

    [HumansFact]
    public async Task CreateShiftAsync_CreatesShift_WhenInputMatchesRotaPeriodAndTeam()
    {
        var (_, rota) = SeedRotaScenario(RotaPeriod.Build);
        await Db.SaveChangesAsync();

        var result = await _service.CreateShiftAsync(new CreateShiftInput(
            rota.Id,
            rota.TeamId,
            "Gate crew",
            -3,
            new LocalTime(8, 30),
            4,
            2,
            5,
            AdminOnly: true,
            IsAllDay: false));

        result.Succeeded.Should().BeTrue();
        result.Message.Should().Be("Shift created.");
        result.ShiftId.Should().NotBeNull();

        var shift = await Db.Shifts.SingleAsync();
        shift.Id.Should().Be(result.ShiftId.Value);
        shift.RotaId.Should().Be(rota.Id);
        shift.Description.Should().Be("Gate crew");
        shift.DayOffset.Should().Be(-3);
        shift.StartTime.Should().Be(new LocalTime(8, 30));
        shift.Duration.Should().Be(Duration.FromHours(4));
        shift.MinVolunteers.Should().Be(2);
        shift.MaxVolunteers.Should().Be(5);
        shift.AdminOnly.Should().BeTrue();
        shift.CreatedAt.Should().Be(TestNow);
        shift.UpdatedAt.Should().Be(TestNow);
    }

    [HumansFact]
    public async Task CreateShiftAsync_ReturnsFailure_WhenDayOffsetOutsideRotaPeriod()
    {
        var (_, rota) = SeedRotaScenario(RotaPeriod.Build);
        await Db.SaveChangesAsync();

        var result = await _service.CreateShiftAsync(new CreateShiftInput(
            rota.Id,
            rota.TeamId,
            "Gate crew",
            0,
            new LocalTime(8, 30),
            4,
            2,
            5,
            AdminOnly: false,
            IsAllDay: false));

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Be("Shift date must fall within the rota's period.");
        Db.Shifts.Should().BeEmpty();
    }

    [HumansFact]
    public async Task UpdateShiftAsync_UpdatesShift_WhenInputMatchesRotaPeriodAndTeam()
    {
        var (_, rota) = SeedRotaScenario(RotaPeriod.Build);
        var shift = SeedShift(rota, -4);
        await Db.SaveChangesAsync();

        var result = await _service.UpdateShiftAsync(new UpdateShiftInput(
            shift.Id,
            rota.TeamId,
            "Updated gate crew",
            -3,
            new LocalTime(9, 15),
            5,
            3,
            6,
            AdminOnly: true));

        result.Succeeded.Should().BeTrue();
        result.Message.Should().Be("Shift updated.");
        result.ShiftId.Should().Be(shift.Id);

        Db.ChangeTracker.Clear();
        var updated = await Db.Shifts.SingleAsync();
        updated.Description.Should().Be("Updated gate crew");
        updated.DayOffset.Should().Be(-3);
        updated.StartTime.Should().Be(new LocalTime(9, 15));
        updated.Duration.Should().Be(Duration.FromHours(5));
        updated.MinVolunteers.Should().Be(3);
        updated.MaxVolunteers.Should().Be(6);
        updated.AdminOnly.Should().BeTrue();
        updated.UpdatedAt.Should().Be(TestNow);
    }

    [HumansFact]
    public async Task UpdateShiftAsync_ReturnsFailure_WhenShiftBelongsToOtherTeam()
    {
        var (_, rota) = SeedRotaScenario(RotaPeriod.Build);
        var shift = SeedShift(rota, -4);
        await Db.SaveChangesAsync();

        var result = await _service.UpdateShiftAsync(new UpdateShiftInput(
            shift.Id,
            Guid.NewGuid(),
            "Updated gate crew",
            -3,
            new LocalTime(9, 15),
            5,
            3,
            6,
            AdminOnly: false));

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Be("Shift not found.");

        Db.ChangeTracker.Clear();
        var unchanged = await Db.Shifts.SingleAsync();
        unchanged.Description.Should().BeNull();
        unchanged.DayOffset.Should().Be(-4);
    }

    [HumansFact(Timeout = 10000)]
    public async Task CreateBuildStrikeShifts_SetsCorrectMinMaxPerDay()
    {
        // Arrange: staffing grid with varying min/max per day
        var (es, rota) = SeedRotaScenario(RotaPeriod.Build);
        await Db.SaveChangesAsync();

        var staffing = new List<DayStaffingInput>
        {
            new(-3, 1, 3),
            new(-2, 4, 8),
            new(-1, 2, 6)
        };

        // Act
        var result = await _service.CreateBuildStrikeShiftsAsync(new ConfigureBuildStrikeStaffingInput(
            rota.Id,
            rota.TeamId,
            staffing));

        result.Succeeded.Should().BeTrue();

        // Assert: each shift has the min/max from its corresponding day in the grid
        var shifts = await Db.Shifts
            .Where(s => s.RotaId == rota.Id)
            .OrderBy(s => s.DayOffset)
            .ToListAsync();

        shifts[0].MinVolunteers.Should().Be(1);
        shifts[0].MaxVolunteers.Should().Be(3);
        shifts[1].MinVolunteers.Should().Be(4);
        shifts[1].MaxVolunteers.Should().Be(8);
        shifts[2].MinVolunteers.Should().Be(2);
        shifts[2].MaxVolunteers.Should().Be(6);
    }

    [HumansFact]
    public async Task CreateBuildStrikeShifts_RejectsEventPeriodRota()
    {
        // Arrange: rota with Period=Event
        var (es, rota) = SeedRotaScenario(RotaPeriod.Event);
        await Db.SaveChangesAsync();

        var staffing = new List<DayStaffingInput>
        {
            new(0, 2, 5)
        };

        var result = await _service.CreateBuildStrikeShiftsAsync(new ConfigureBuildStrikeStaffingInput(
            rota.Id,
            rota.TeamId,
            staffing));

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Be("Build/strike shift generation is only for Build or Strike rotas.");
        Db.Shifts.Should().BeEmpty();
    }

    // ============================================================
    // GenerateEventShiftsAsync
    // ============================================================

    [HumansFact]
    public async Task GenerateEventShifts_CreatesCartesianProduct()
    {
        // Arrange: event rota, days 0-2, slots [(08:00, 4h), (14:00, 4h)]
        var (es, rota) = SeedRotaScenario(RotaPeriod.Event);
        await Db.SaveChangesAsync();

        var timeSlots = new List<ShiftTimeSlotInput>
        {
            new(new LocalTime(8, 0), 4),
            new(new LocalTime(14, 0), 4)
        };

        // Act
        var result = await _service.GenerateEventShiftsAsync(new GenerateEventShiftsInput(
            rota.Id,
            rota.TeamId,
            0,
            2,
            timeSlots,
            2,
            5));

        result.Succeeded.Should().BeTrue();
        result.CreatedCount.Should().Be(6);

        // Assert: 6 shifts (3 days × 2 slots), none IsAllDay
        var shifts = await Db.Shifts.Where(s => s.RotaId == rota.Id).ToListAsync();
        shifts.Should().HaveCount(6);
        shifts.Should().AllSatisfy(s => s.IsAllDay.Should().BeFalse());

        // Verify correct day offsets
        shifts.Select(s => s.DayOffset).Distinct().Should().BeEquivalentTo([0, 1, 2]);

        // Verify correct start times
        var startTimes = shifts.Select(s => s.StartTime).Distinct().ToList();
        startTimes.Should().HaveCount(2);
        startTimes.Should().Contain(new LocalTime(8, 0));
        startTimes.Should().Contain(new LocalTime(14, 0));
    }

    [HumansFact]
    public async Task GenerateEventShifts_RejectsBuildPeriodRota()
    {
        // Arrange: rota with Period=Build
        var (es, rota) = SeedRotaScenario(RotaPeriod.Build);
        await Db.SaveChangesAsync();

        var timeSlots = new List<ShiftTimeSlotInput>
        {
            new(new LocalTime(8, 0), 4)
        };

        var result = await _service.GenerateEventShiftsAsync(new GenerateEventShiftsInput(
            rota.Id,
            rota.TeamId,
            0,
            2,
            timeSlots,
            2,
            5));

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Be("Event shift generation is only for Event-period rotas.");
        Db.Shifts.Should().BeEmpty();
    }

    // ============================================================
    // GetBrowseShiftsAsync — includeSignups
    // ============================================================

    [HumansFact]
    public async Task GetBrowseShifts_IncludeSignups_ReturnsConfirmedAndPendingOnly()
    {
        // Arrange
        var (es, rota) = SeedRotaScenario(RotaPeriod.Event);
        var shift = SeedShift(rota, dayOffset: 1);
        var confirmedUser = SeedUser("Alice");
        var pendingUser = SeedUser("Bob");
        var bailedUser = SeedUser("Charlie");

        SeedSignup(shift, confirmedUser, SignupStatus.Confirmed);
        SeedSignup(shift, pendingUser, SignupStatus.Pending);
        SeedSignup(shift, bailedUser, SignupStatus.Bailed);
        await Db.SaveChangesAsync();

        // Act
        var results = await _service.GetBrowseShiftsAsync(es.Id, includeSignups: true);

        // Assert: only Confirmed and Pending signups returned
        results.Should().HaveCount(1);
        var signups = results[0].Signups;
        signups.Should().HaveCount(2);
        signups.Select(s => s.DisplayName).Should().BeEquivalentTo("Alice", "Bob");
    }

    [HumansFact]
    public async Task GetBrowseShifts_IncludeSignups_ConfirmedBeforePending()
    {
        // Arrange
        var (es, rota) = SeedRotaScenario(RotaPeriod.Event);
        var shift = SeedShift(rota, dayOffset: 1);
        var confirmedUser = SeedUser("Zara");
        var pendingUser = SeedUser("Alice");

        SeedSignup(shift, confirmedUser, SignupStatus.Confirmed);
        SeedSignup(shift, pendingUser, SignupStatus.Pending);
        await Db.SaveChangesAsync();

        // Act
        var results = await _service.GetBrowseShiftsAsync(es.Id, includeSignups: true);

        // Assert: Confirmed (Zara) first, then Pending (Alice), regardless of name order
        var signups = results[0].Signups;
        signups.Should().HaveCount(2);
        signups[0].DisplayName.Should().Be("Zara");
        signups[0].Status.Should().Be(SignupStatus.Confirmed);
        signups[1].DisplayName.Should().Be("Alice");
        signups[1].Status.Should().Be(SignupStatus.Pending);
    }

    [HumansFact]
    public async Task GetBrowseShifts_WithoutIncludeSignups_ReturnsEmptySignups()
    {
        // Arrange
        var (es, rota) = SeedRotaScenario(RotaPeriod.Event);
        var shift = SeedShift(rota, dayOffset: 1);
        var user = SeedUser("Alice");
        SeedSignup(shift, user, SignupStatus.Confirmed);
        await Db.SaveChangesAsync();

        // Act
        var results = await _service.GetBrowseShiftsAsync(es.Id, includeSignups: false);

        // Assert: signups list is empty even though shift has signups
        results.Should().HaveCount(1);
        results[0].Signups.Should().BeEmpty();
    }

    [HumansFact]
    public async Task GetBrowseShifts_PriorityOnly_FiltersToImportantOrEssentialOrUnderstaffed()
    {
        // Arrange three rotas in the same event:
        //   - Normal priority + fully staffed     → EXCLUDED
        //   - Important priority + fully staffed  → INCLUDED (priority)
        //   - Normal priority + understaffed      → INCLUDED (some shift below MinVolunteers)
        var (es, normalStaffedRota) = SeedRotaScenario(RotaPeriod.Event);

        var importantRota = new Rota
        {
            Id = Guid.NewGuid(),
            EventSettingsId = es.Id,
            TeamId = normalStaffedRota.TeamId,
            Name = "Important Rota",
            Priority = ShiftPriority.Important,
            Policy = SignupPolicy.Public,
            Period = RotaPeriod.Event,
            CreatedAt = TestNow,
            UpdatedAt = TestNow,
            EventSettings = es
        };
        Db.Rotas.Add(importantRota);

        var understaffedRota = new Rota
        {
            Id = Guid.NewGuid(),
            EventSettingsId = es.Id,
            TeamId = normalStaffedRota.TeamId,
            Name = "Understaffed Normal Rota",
            Priority = ShiftPriority.Normal,
            Policy = SignupPolicy.Public,
            Period = RotaPeriod.Event,
            CreatedAt = TestNow,
            UpdatedAt = TestNow,
            EventSettings = es
        };
        Db.Rotas.Add(understaffedRota);

        // Normal+staffed: MinVolunteers=2, two confirmed signups → meets minimum.
        var normalShift = SeedShift(normalStaffedRota, dayOffset: 1);
        SeedSignup(normalShift, SeedUser("NormA"), SignupStatus.Confirmed);
        SeedSignup(normalShift, SeedUser("NormB"), SignupStatus.Confirmed);

        // Important+staffed: still INCLUDED because of priority.
        var importantShift = SeedShift(importantRota, dayOffset: 1);
        SeedSignup(importantShift, SeedUser("ImpA"), SignupStatus.Confirmed);
        SeedSignup(importantShift, SeedUser("ImpB"), SignupStatus.Confirmed);

        // Normal+understaffed: zero confirmed signups, MinVolunteers=2 → understaffed → INCLUDED.
        var understaffedShift = SeedShift(understaffedRota, dayOffset: 1);

        await Db.SaveChangesAsync();

        // Act
        var results = await _service.GetBrowseShiftsAsync(es.Id, priorityOnly: true);

        // Assert: only the Important rota's shift and the understaffed Normal rota's shift remain.
        results.Select(r => r.Shift.RotaId).Should().BeEquivalentTo([
            importantRota.Id,
            understaffedRota.Id
        ]);
    }

    [HumansFact]
    public async Task GetBrowseShifts_DepartmentFilter_IncludesUnpromotedSubTeamRotas()
    {
        // Parent department + two children: one unpromoted (rolls up into parent's pie),
        // one promoted to its own pie. Filtering by the parent must return the parent's
        // shifts AND the unpromoted child's shifts, but NOT the promoted child's shifts —
        // the promoted child has its own pie and is filtered to separately.
        var (es, parentRota) = SeedRotaScenario(RotaPeriod.Event);
        var parentTeamId = parentRota.TeamId;

        var unpromotedSubTeam = new Team
        {
            Id = Guid.NewGuid(),
            Name = "Sober Camp",
            Slug = "sober-camp",
            SystemTeamType = SystemTeamType.None,
            ParentTeamId = parentTeamId,
            IsPromotedToDirectory = false,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        };
        var promotedSubTeam = new Team
        {
            Id = Guid.NewGuid(),
            Name = "Interpreters",
            Slug = "interpreters",
            SystemTeamType = SystemTeamType.None,
            ParentTeamId = parentTeamId,
            IsPromotedToDirectory = true,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        };
        Db.Teams.Add(unpromotedSubTeam);
        Db.Teams.Add(promotedSubTeam);

        var unpromotedRota = new Rota
        {
            Id = Guid.NewGuid(),
            EventSettingsId = es.Id,
            TeamId = unpromotedSubTeam.Id,
            Name = "Sober Camp Rota",
            Priority = ShiftPriority.Normal,
            Policy = SignupPolicy.Public,
            Period = RotaPeriod.Event,
            CreatedAt = TestNow,
            UpdatedAt = TestNow,
            EventSettings = es
        };
        var promotedRota = new Rota
        {
            Id = Guid.NewGuid(),
            EventSettingsId = es.Id,
            TeamId = promotedSubTeam.Id,
            Name = "Interpreters Rota",
            Priority = ShiftPriority.Normal,
            Policy = SignupPolicy.Public,
            Period = RotaPeriod.Event,
            CreatedAt = TestNow,
            UpdatedAt = TestNow,
            EventSettings = es
        };
        Db.Rotas.Add(unpromotedRota);
        Db.Rotas.Add(promotedRota);

        SeedShift(parentRota, dayOffset: 1);
        SeedShift(unpromotedRota, dayOffset: 1);
        SeedShift(promotedRota, dayOffset: 1);
        await Db.SaveChangesAsync();

        var results = await _service.GetBrowseShiftsAsync(es.Id, departmentId: parentTeamId);

        results.Select(r => r.Shift.RotaId).Should().BeEquivalentTo([
            parentRota.Id,
            unpromotedRota.Id
        ]);
    }

    [HumansFact]
    public async Task GetBrowseShifts_DepartmentFilter_PromotedSubTeam_ReturnsOnlyOwnRotas()
    {
        // Filtering by a promoted sub-team must return only its own rotas — not the
        // parent's, not the sibling unpromoted sub-team's. The promoted sub-team has
        // its own pie and behaves as a first-class department for filtering.
        var (es, parentRota) = SeedRotaScenario(RotaPeriod.Event);
        var parentTeamId = parentRota.TeamId;

        var promotedSubTeam = new Team
        {
            Id = Guid.NewGuid(),
            Name = "Interpreters",
            Slug = "interpreters",
            SystemTeamType = SystemTeamType.None,
            ParentTeamId = parentTeamId,
            IsPromotedToDirectory = true,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        };
        Db.Teams.Add(promotedSubTeam);

        var promotedRota = new Rota
        {
            Id = Guid.NewGuid(),
            EventSettingsId = es.Id,
            TeamId = promotedSubTeam.Id,
            Name = "Interpreters Rota",
            Priority = ShiftPriority.Normal,
            Policy = SignupPolicy.Public,
            Period = RotaPeriod.Event,
            CreatedAt = TestNow,
            UpdatedAt = TestNow,
            EventSettings = es
        };
        Db.Rotas.Add(promotedRota);

        SeedShift(parentRota, dayOffset: 1);
        SeedShift(promotedRota, dayOffset: 1);
        await Db.SaveChangesAsync();

        var results = await _service.GetBrowseShiftsAsync(es.Id, departmentId: promotedSubTeam.Id);

        results.Select(r => r.Shift.RotaId).Should().BeEquivalentTo([promotedRota.Id]);
    }

    [HumansFact]
    public async Task GetUrgentShifts_DepartmentFilter_IncludesUnpromotedSubTeamRotas()
    {
        // Mirrors the browse-page rollup: filtering urgent shifts by a parent department
        // must include non-promoted sub-team rotas (which roll up into the parent's pie)
        // but exclude promoted sub-teams (which have their own filter).
        var (es, parentRota) = SeedRotaScenario(RotaPeriod.Event);
        var parentTeamId = parentRota.TeamId;

        var unpromotedSubTeam = new Team
        {
            Id = Guid.NewGuid(),
            Name = "Sober Camp",
            Slug = "sober-camp",
            SystemTeamType = SystemTeamType.None,
            ParentTeamId = parentTeamId,
            IsPromotedToDirectory = false,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        };
        var promotedSubTeam = new Team
        {
            Id = Guid.NewGuid(),
            Name = "Interpreters",
            Slug = "interpreters",
            SystemTeamType = SystemTeamType.None,
            ParentTeamId = parentTeamId,
            IsPromotedToDirectory = true,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        };
        Db.Teams.Add(unpromotedSubTeam);
        Db.Teams.Add(promotedSubTeam);

        var unpromotedRota = new Rota
        {
            Id = Guid.NewGuid(),
            EventSettingsId = es.Id,
            TeamId = unpromotedSubTeam.Id,
            Name = "Sober Camp Rota",
            Priority = ShiftPriority.Normal,
            Policy = SignupPolicy.Public,
            Period = RotaPeriod.Event,
            CreatedAt = TestNow,
            UpdatedAt = TestNow,
            EventSettings = es
        };
        var promotedRota = new Rota
        {
            Id = Guid.NewGuid(),
            EventSettingsId = es.Id,
            TeamId = promotedSubTeam.Id,
            Name = "Interpreters Rota",
            Priority = ShiftPriority.Normal,
            Policy = SignupPolicy.Public,
            Period = RotaPeriod.Event,
            CreatedAt = TestNow,
            UpdatedAt = TestNow,
            EventSettings = es
        };
        Db.Rotas.Add(unpromotedRota);
        Db.Rotas.Add(promotedRota);

        SeedShift(parentRota, dayOffset: 1);
        SeedShift(unpromotedRota, dayOffset: 1);
        SeedShift(promotedRota, dayOffset: 1);
        await Db.SaveChangesAsync();

        var results = await _service.GetUrgentShiftsAsync(es.Id, departmentId: parentTeamId);

        results.Select(r => r.Shift.RotaId).Should().BeEquivalentTo([
            parentRota.Id,
            unpromotedRota.Id
        ]);
    }

    // ============================================================
    // Helpers
    // ============================================================

    private Shift SeedShift(Rota rota, int dayOffset)
    {
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            RotaId = rota.Id,
            DayOffset = dayOffset,
            StartTime = new LocalTime(8, 0),
            Duration = Duration.FromHours(4),
            MinVolunteers = 2,
            MaxVolunteers = 5,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        };
        Db.Shifts.Add(shift);
        return shift;
    }

    private void SeedSignup(Shift shift, User user, SignupStatus status)
    {
        var signup = new ShiftSignup
        {
            Id = Guid.NewGuid(),
            ShiftId = shift.Id,
            UserId = user.Id,
            Status = status,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        };
        Db.ShiftSignups.Add(signup);
    }

    private (EventSettings es, Rota rota) SeedRotaScenario(RotaPeriod period)
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

        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = "Test Department",
            Slug = "test-dept",
            SystemTeamType = SystemTeamType.None,
            ParentTeamId = null,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        };
        Db.Teams.Add(team);

        var rota = new Rota
        {
            Id = Guid.NewGuid(),
            EventSettingsId = es.Id,
            TeamId = team.Id,
            Name = "Test Rota",
            Priority = ShiftPriority.Normal,
            Policy = SignupPolicy.Public,
            Period = period,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        };
        Db.Rotas.Add(rota);

        rota.EventSettings = es;

        return (es, rota);
    }

    // ============================================================
    // IsDeptCoordinatorAsync — sub-team manager support
    // ============================================================

    [HumansFact]
    public async Task IsDeptCoordinatorAsync_SubTeamManager_ReturnsTrue_ForOwnSubTeam()
    {
        var userId = Guid.NewGuid();
        var subTeamId = Guid.NewGuid();

        // User coordinates subTeam
        _teamService.GetUserCoordinatedTeamIdsAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { subTeamId });

        var result = await _service.IsDeptCoordinatorAsync(userId, subTeamId);

        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task IsDeptCoordinatorAsync_SubTeamManager_ReturnsFalse_ForSiblingSubTeam()
    {
        var userId = Guid.NewGuid();
        var subTeamAId = Guid.NewGuid();
        var subTeamBId = Guid.NewGuid();
        var departmentId = Guid.NewGuid();

        // User coordinates subTeamA only
        _teamService.GetUserCoordinatedTeamIdsAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { subTeamAId });

        // subTeamB's parent is department (not in coordinated list)
        _teamService.GetTeamByIdAsync(subTeamBId, Arg.Any<CancellationToken>())
            .Returns(new Team
            {
                Id = subTeamBId,
                Name = "SubTeamB",
                Slug = "subteam-b",
                SystemTeamType = SystemTeamType.None,
                ParentTeamId = departmentId,
                CreatedAt = TestNow,
                UpdatedAt = TestNow
            });

        var result = await _service.IsDeptCoordinatorAsync(userId, subTeamBId);

        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task IsDeptCoordinatorAsync_SubTeamManager_ReturnsFalse_ForParentDepartment()
    {
        var userId = Guid.NewGuid();
        var subTeamId = Guid.NewGuid();
        var departmentId = Guid.NewGuid();

        // User coordinates subTeam only
        _teamService.GetUserCoordinatedTeamIdsAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { subTeamId });

        // Department has no parent
        _teamService.GetTeamByIdAsync(departmentId, Arg.Any<CancellationToken>())
            .Returns(new Team
            {
                Id = departmentId,
                Name = "Department",
                Slug = "department",
                SystemTeamType = SystemTeamType.None,
                ParentTeamId = null,
                CreatedAt = TestNow,
                UpdatedAt = TestNow
            });

        var result = await _service.IsDeptCoordinatorAsync(userId, departmentId);

        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task IsDeptCoordinatorAsync_DepartmentCoordinator_ReturnsTrue_ForChildSubTeam()
    {
        var userId = Guid.NewGuid();
        var departmentId = Guid.NewGuid();
        var subTeamId = Guid.NewGuid();

        // User coordinates department
        _teamService.GetUserCoordinatedTeamIdsAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { departmentId });

        // subTeam's parent is department
        _teamService.GetTeamByIdAsync(subTeamId, Arg.Any<CancellationToken>())
            .Returns(new Team
            {
                Id = subTeamId,
                Name = "SubTeam",
                Slug = "subteam",
                SystemTeamType = SystemTeamType.None,
                ParentTeamId = departmentId,
                CreatedAt = TestNow,
                UpdatedAt = TestNow
            });

        var result = await _service.IsDeptCoordinatorAsync(userId, subTeamId);

        result.Should().BeTrue();
    }

    // ============================================================
    // GetOverallCoverageAsync
    // ============================================================

    [HumansFact]
    public async Task GetOverallCoverageAsync_ReturnsCorrectFilledTotalRatio()
    {
        // Arrange: active event with 10 slots across 10 shifts, 7 confirmed signups
        var (es, rota) = SeedRotaScenario(RotaPeriod.Event);

        var shifts = Enumerable.Range(0, 10).Select(_ => new Shift
        {
            Id = Guid.NewGuid(),
            RotaId = rota.Id,
            DayOffset = 0,
            StartTime = new LocalTime(8, 0),
            Duration = Duration.FromHours(4),
            MinVolunteers = 0,
            MaxVolunteers = 1,
            AdminOnly = false,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        }).ToList();
        Db.Shifts.AddRange(shifts);

        // 7 confirmed signups on the first 7 shifts
        var userId = Guid.NewGuid();
        for (var i = 0; i < 7; i++)
        {
            Db.ShiftSignups.Add(new ShiftSignup
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                ShiftId = shifts[i].Id,
                Status = SignupStatus.Confirmed,
                CreatedAt = TestNow,
                UpdatedAt = TestNow
            });
        }

        await Db.SaveChangesAsync();

        // Act
        var (filled, total, ratio) = await _service.GetOverallCoverageAsync();

        // Assert
        filled.Should().Be(7);
        total.Should().Be(10);
        ratio.Should().Be(0.7);
    }

    [HumansFact]
    public async Task GetOverallCoverageAsync_ReturnsZero_WhenNoActiveEvent()
    {
        // Arrange: no event settings seeded → no active event

        // Act
        var (filled, total, ratio) = await _service.GetOverallCoverageAsync();

        // Assert
        filled.Should().Be(0);
        total.Should().Be(0);
        ratio.Should().Be(0d);
    }

    // ============================================================
    // EventSettings singleton — Shifts.md invariant line 229
    // ============================================================

    [HumansFact]
    public async Task CreateEventSettingsAsync_WithIsActiveTrue_WhenActiveAlreadyExists_Throws()
    {
        // Arrange: one active EventSettings already in the DB
        var existing = new EventSettings
        {
            Id = Guid.NewGuid(),
            EventName = "Existing 2026",
            TimeZoneId = "Europe/Madrid",
            GateOpeningDate = new LocalDate(2026, 7, 1),
            BuildStartOffset = -14,
            EventEndOffset = 6,
            StrikeEndOffset = 9,
            IsActive = true,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        };
        Db.EventSettings.Add(existing);
        await Db.SaveChangesAsync();

        var second = new EventSettings
        {
            Id = Guid.NewGuid(),
            EventName = "Second 2027",
            TimeZoneId = "Europe/Madrid",
            GateOpeningDate = new LocalDate(2027, 7, 1),
            BuildStartOffset = -14,
            EventEndOffset = 6,
            StrikeEndOffset = 9,
            IsActive = true,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        };

        // Act + Assert: CreateAsync rejects the second IsActive=true row
        var act = () => _service.CreateAsync(second);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*one*active*");
    }

    [HumansFact]
    public async Task UpdateEventSettingsAsync_SettingIsActiveTrue_WhenOtherActiveExists_Throws()
    {
        // Arrange: one active EventSettings already exists, plus an inactive
        // one we want to flip to active.
        var existing = new EventSettings
        {
            Id = Guid.NewGuid(),
            EventName = "Active 2026",
            TimeZoneId = "Europe/Madrid",
            GateOpeningDate = new LocalDate(2026, 7, 1),
            BuildStartOffset = -14,
            EventEndOffset = 6,
            StrikeEndOffset = 9,
            IsActive = true,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        };
        Db.EventSettings.Add(existing);

        var inactive = new EventSettings
        {
            Id = Guid.NewGuid(),
            EventName = "Inactive 2027",
            TimeZoneId = "Europe/Madrid",
            GateOpeningDate = new LocalDate(2027, 7, 1),
            BuildStartOffset = -14,
            EventEndOffset = 6,
            StrikeEndOffset = 9,
            IsActive = false,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        };
        Db.EventSettings.Add(inactive);
        await Db.SaveChangesAsync();

        // Act: flip the inactive row to IsActive=true
        inactive.IsActive = true;
        var act = () => _service.UpdateAsync(inactive);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*one*active*");
    }

    // Medical-data gating moved off GetShiftProfileAsync: MedicalConditions is now
    // a Profile field on the cached UserInfo, gated per render/serialize surface by
    // the MedicalDataViewer policy (see the dietary-medical-to-profile migration).
    // The old includeMedical strip tests were removed with that parameter.

    // ============================================================
    // Rota delete — Shifts.md trigger line 262
    // ============================================================

    [HumansFact]
    public async Task DeleteRotaAsync_WithConfirmedSignup_Throws()
    {
        // Arrange: rota with one Confirmed signup
        var (es, rota) = SeedRotaScenario(RotaPeriod.Event);
        var shift = SeedShift(rota, dayOffset: 1);
        var user = SeedUser("Alice");
        SeedSignup(shift, user, SignupStatus.Confirmed);
        await Db.SaveChangesAsync();

        // Act + Assert
        var act = () => _service.DeleteRotaAsync(rota.Id);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*confirmed*");
    }

    [HumansFact]
    public async Task DeleteRotaAsync_WithOnlyPendingSignups_CancelsThemAndDeletes()
    {
        // Arrange: rota with two Pending signups (no Confirmed)
        var (es, rota) = SeedRotaScenario(RotaPeriod.Event);
        var shift = SeedShift(rota, dayOffset: 1);
        var user1 = SeedUser("Bob");
        var user2 = SeedUser("Carol");
        var pending1 = new ShiftSignup
        {
            Id = Guid.NewGuid(),
            ShiftId = shift.Id,
            UserId = user1.Id,
            Status = SignupStatus.Pending,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        };
        var pending2 = new ShiftSignup
        {
            Id = Guid.NewGuid(),
            ShiftId = shift.Id,
            UserId = user2.Id,
            Status = SignupStatus.Pending,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        };
        await Db.ShiftSignups.AddRangeAsync(pending1, pending2);
        await Db.SaveChangesAsync();

        // Act
        await _service.DeleteRotaAsync(rota.Id);

        // Assert: rota gone, pending signups removed by cascade-delete. We query
        // through the service so we hit the same factory-managed context the
        // delete used (the test's Db caches the tracked rota).
        (await _service.GetRotaByIdAsync(rota.Id)).Should().BeNull();
        (await Db.ShiftSignups
            .AsNoTracking()
            .Where(s => s.Id == pending1.Id || s.Id == pending2.Id)
            .ToListAsync())
            .Should().BeEmpty();
    }

    // ============================================================
    // Rota move audit — Shifts.md trigger line 261
    // ============================================================

    [HumansFact]
    public async Task MoveRotaToTeamAsync_WritesRotaMovedToTeamAuditEntry()
    {
        // Arrange: rota currently on team A, target team B (no parent).
        var (es, rota) = SeedRotaScenario(RotaPeriod.Event);
        await Db.SaveChangesAsync();

        var targetTeamId = Guid.NewGuid();
        var actorUserId = Guid.NewGuid();

        var sourceTeam = await Db.Teams.FirstAsync(t => t.Id == rota.TeamId);
        _teamService.GetTeamByIdAsync(rota.TeamId, Arg.Any<CancellationToken>())
            .Returns(sourceTeam);
        _teamService.GetTeamByIdAsync(targetTeamId, Arg.Any<CancellationToken>())
            .Returns(new Team
            {
                Id = targetTeamId,
                Name = "Target Department",
                Slug = "target-dept",
                SystemTeamType = SystemTeamType.None,
                ParentTeamId = null,
                CreatedAt = TestNow,
                UpdatedAt = TestNow
            });

        // Act
        var result = await _service.MoveRotaToTeamAsync(new MoveRotaInput(
            rota.Id,
            rota.TeamId,
            targetTeamId,
            actorUserId));

        result.Succeeded.Should().BeTrue();
        result.Message.Should().Be("Rota 'Test Rota' moved to Target Department.");
        result.RedirectSlug.Should().Be("target-dept");

        // Assert: audit entry written with action=RotaMovedToTeam, related team=target.
        await AuditLog.Received(1).LogAsync(
            AuditAction.RotaMovedToTeam,
            nameof(Rota),
            rota.Id,
            Arg.Any<string>(),
            actorUserId,
            targetTeamId,
            nameof(Team));
    }
}
