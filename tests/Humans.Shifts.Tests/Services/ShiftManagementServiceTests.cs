using Humans.Shifts.Services.Dtos;
using Humans.Shifts.Domain;
using Humans.Auth.Contracts;
using Humans.Teams.Domain;
using AwesomeAssertions;
using Humans.Shifts.Services;
using Humans.Shifts.Contracts;
using Humans.Teams.Contracts;
using Humans.Shifts.Tests.Infrastructure;
using Humans.Base.Enums;
using Humans.Base.Extensions;
using Humans.Shifts.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NodaTime;
using NSubstitute;
using Humans.Users.Contracts;
using Humans.AuditLog.Contracts;

using Humans.Shifts.Models;

namespace Humans.Shifts.Tests.Services;

public sealed class ShiftManagementServiceTests : ShiftsTestHarness
{
    private readonly ITeamService _teamService;
    private readonly IUserService _userService;
    private readonly IRoleAssignmentService _roleAssignmentService;
    private readonly Humans.Settings.Contracts.ISettingsService _settingsService = Substitute.For<Humans.Settings.Contracts.ISettingsService>();
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
        _userService.StubGetUserInfosFromContext(Db);

        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyDictionary<Guid, TeamInfo>>(
                TeamsDb.Teams.AsEnumerable().ToDictionary(
                    t => t.Id,
                    ToTeamInfo)));
        _teamService.GetTeamAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var id = ci.Arg<Guid>();
                var team = TeamsDb.Teams.AsEnumerable().FirstOrDefault(t => t.Id == id);
                return Task.FromResult(team is null ? null : ToTeamInfo(team));
            });

        var serviceProvider = new ServiceLocatorBuilder()
            .With(_teamService)
            .With(_userService)
            .With(_userService)
            .With(_roleAssignmentService)
            .With(_settingsService)
            .Build();

        var repo = new ShiftRepository(ShiftsDbFactory, ShiftsDb, Clock);

        _service = new ShiftManagementService(
            repo,
            AuditLog,
            AdminAuthorization,
            serviceProvider,
            Cache,
            new ShiftViewProbe().Cache,
            NewCalendarResolver(),
            Clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ShiftManagementService>.Instance);
    }

    private static TeamInfo ToTeamInfo(Team team) =>
        new(
            team.Id, team.Name, team.Description, team.Slug,
            team.IsActive, team.IsSystemTeam, team.SystemTeamType, team.RequiresApproval,
            team.IsPublicPage, team.IsHidden, team.IsPromotedToDirectory, team.CreatedAt,
            Members: [],
            ParentTeamId: team.ParentTeamId);

    [HumansTheory]
    [Xunit.InlineData(true, true)]
    [Xunit.InlineData(true, false)]
    [Xunit.InlineData(false, true)]
    [Xunit.InlineData(false, false)]
    public async Task RotaWriteFailure_EvictsCachedRotaBeforeOrAfterCommit(bool create, bool failureAfterRotaSave)
    {
        var (settings, existing) = SeedRotaScenario(RotaPeriod.Event);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);
        var rota = create ? new Rota
        {
            Id = Guid.NewGuid(),
            EventSettingsId = settings.Id,
            TeamId = existing.TeamId,
            Name = "New rota",
            CreatedAt = TestNow,
            UpdatedAt = TestNow,
        } : existing;
        var originalName = rota.Name;
        rota.Name = "Changed rota";
        var real = new ShiftRepository(ShiftsDbFactory, ShiftsDb, Clock);
        var repo = Substitute.For<IShiftManagementRepository>();
        repo.GetEventSettingsByIdAsync(settings.Id, Arg.Any<CancellationToken>()).Returns(settings);
        var failure = new IOException("Persistence acknowledgement failed");
        repo.SaveRotaAsync(rota, Arg.Any<EntityMutationMode>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<Guid>?>())
            .Returns(async call =>
            {
                if (!failureAfterRotaSave) throw failure;
                await real.SaveRotaAsync(rota, call.Arg<EntityMutationMode>(), call.Arg<CancellationToken>(),
                    call.Arg<IReadOnlyList<Guid>?>());
                throw failure;
            });
        repo.GetRotaAsync(rota.Id, RotaReadShape.None, Arg.Any<CancellationToken>()).Returns(rota);
        var views = new ShiftViewProbe();
        await views.PrimeRotaAsync(rota.Id);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new ShiftManagementService(
            repo, AuditLog, AdminAuthorization, new ServiceLocatorBuilder().With(_teamService).Build(),
            cache, views.Cache, NewCalendarResolver(), Clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ShiftManagementService>.Instance);
        Func<Task> mutate = () => create
            ? service.CreateRotaAsync(rota, [Guid.NewGuid()])
            : service.UpdateRotaAsync(new UpdateRotaInput(
                rota.Id, rota.Name, rota.Description, rota.PracticalInfo, rota.Priority,
                rota.Policy, rota.Period, rota.IsVisibleToVolunteers, [Guid.NewGuid()]));

        (await mutate.Should().ThrowAsync<IOException>()).Which.Should().BeSameAs(failure);
        (await views.IsRotaCachedAsync(rota.Id)).Should().BeFalse();
        var saved = await real.GetRotaAsync(rota.Id, RotaReadShape.None, Xunit.TestContext.Current.CancellationToken);
        if (create && !failureAfterRotaSave) saved.Should().BeNull();
        else saved!.Name.Should().Be(failureAfterRotaSave ? "Changed rota" : originalName);
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task DeleteEvent_EvictsCachesAfterDeleteCompletion(bool saveFails)
    {
        var eventId = Guid.NewGuid();
        var repo = Substitute.For<IShiftManagementRepository>();
        var pending = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        repo.DeleteEventCascadeAsync(eventId, Arg.Any<CancellationToken>()).Returns(pending.Task);
        var views = new ShiftViewProbe();
        await views.PrimeUserAsync(Guid.NewGuid());
        await views.PrimeRotaAsync(Guid.NewGuid());
        using var cache = new MemoryCache(new MemoryCacheOptions());
        cache.Set(ShiftManagementService.OverviewCacheKey(eventId, null), new object());
        var service = new ShiftManagementService(repo, AuditLog, AdminAuthorization,
            new ServiceLocatorBuilder().Build(), cache, views.Cache, NewCalendarResolver(), Clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<ShiftManagementService>.Instance);
        var failure = new IOException("Delete completion is uncertain");

        var deletion = service.DeleteEventAsync(eventId, Xunit.TestContext.Current.CancellationToken);
        views.Evictions.Should().Be(0);
        cache.TryGetValue(ShiftManagementService.OverviewCacheKey(eventId, null), out _).Should().BeTrue();
        if (saveFails) pending.SetException(failure);
        else pending.SetResult(1);

        Exception? error = null;
        try { await deletion; }
        catch (Exception ex) { error = ex; }
        if (saveFails) error.Should().BeSameAs(failure);
        else { error.Should().BeNull(); (await deletion).Should().Be(1); }
        views.Cache.UserCacheStats.BulkInvalidations.Should().Be(1);
        views.Cache.RotaCacheStats.BulkInvalidations.Should().Be(1);
        cache.TryGetValue(ShiftManagementService.OverviewCacheKey(eventId, null), out _).Should().BeFalse();
    }

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
            new ShiftViewProbe().Cache,
            NewCalendarResolver(),
            Clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ShiftManagementService>.Instance);

        var deleted = await service.DeleteEventAsync(eventId, Xunit.TestContext.Current.CancellationToken);

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
        var (_, rota) = SeedRotaScenario(RotaPeriod.Build);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var staffing = new List<DayStaffingInput>
        {
            new(-3, 2, 5),
            new(-2, 2, 5),
            new(-1, 2, 5)
        };

        var result = await _service.CreateBuildStrikeShiftsAsync(new ConfigureBuildStrikeStaffingInput(
            rota.Id,
            rota.TeamId,
            staffing));

        result.Succeeded.Should().BeTrue();
        result.CreatedCount.Should().Be(3);

        // StartTime/Duration are stored as the midnight/24h sentinel (don't-care for IsAllDay rows).
        var shifts = await ShiftsDb.Shifts.Where(s => s.RotaId == rota.Id).ToListAsync(Xunit.TestContext.Current.CancellationToken);
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
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

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

        var shift = await ShiftsDb.Shifts.SingleAsync(Xunit.TestContext.Current.CancellationToken);
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
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

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
        ShiftsDb.Shifts.Should().BeEmpty();
    }

    [HumansFact]
    public async Task UpdateShiftAsync_UpdatesShift_WhenInputMatchesRotaPeriodAndTeam()
    {
        var (_, rota) = SeedRotaScenario(RotaPeriod.Build);
        var shift = SeedShift(rota, -4);
        SeedSignup(shift, SeedUser("Builder"), SignupStatus.Confirmed);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

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

        ShiftsDb.ChangeTracker.Clear();
        var updated = await ShiftsDb.Shifts.SingleAsync(Xunit.TestContext.Current.CancellationToken);
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
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

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

        ShiftsDb.ChangeTracker.Clear();
        var unchanged = await ShiftsDb.Shifts.SingleAsync(Xunit.TestContext.Current.CancellationToken);
        unchanged.Description.Should().BeNull();
        unchanged.DayOffset.Should().Be(-4);
    }

    [HumansFact(Timeout = 10000)]
    public async Task CreateBuildStrikeShifts_SetsCorrectMinMaxPerDay()
    {
        var (_, rota) = SeedRotaScenario(RotaPeriod.Build);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var staffing = new List<DayStaffingInput>
        {
            new(-3, 1, 3),
            new(-2, 4, 8),
            new(-1, 2, 6)
        };

        var result = await _service.CreateBuildStrikeShiftsAsync(new ConfigureBuildStrikeStaffingInput(
            rota.Id,
            rota.TeamId,
            staffing));

        result.Succeeded.Should().BeTrue();

        var shifts = await ShiftsDb.Shifts
            .Where(s => s.RotaId == rota.Id)
            .OrderBy(s => s.DayOffset)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);

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
        var (_, rota) = SeedRotaScenario(RotaPeriod.Event);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

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
        ShiftsDb.Shifts.Should().BeEmpty();
    }

    [HumansFact]
    public async Task GenerateEventShifts_CreatesCartesianProduct()
    {
        var (_, rota) = SeedRotaScenario(RotaPeriod.Event);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var timeSlots = new List<ShiftTimeSlotInput>
        {
            new(new LocalTime(8, 0), 4),
            new(new LocalTime(14, 0), 4)
        };

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

        var shifts = await ShiftsDb.Shifts.Where(s => s.RotaId == rota.Id).ToListAsync(Xunit.TestContext.Current.CancellationToken);
        shifts.Should().HaveCount(6);
        shifts.Should().AllSatisfy(s => s.IsAllDay.Should().BeFalse());

        shifts.Select(s => s.DayOffset).Distinct().Should().BeEquivalentTo([0, 1, 2]);

        var startTimes = shifts.Select(s => s.StartTime).Distinct().ToList();
        startTimes.Should().HaveCount(2);
        startTimes.Should().Contain(new LocalTime(8, 0));
        startTimes.Should().Contain(new LocalTime(14, 0));
    }

    [HumansFact]
    public async Task GenerateEventShifts_RejectsBuildPeriodRota()
    {
        var (_, rota) = SeedRotaScenario(RotaPeriod.Build);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

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
        ShiftsDb.Shifts.Should().BeEmpty();
    }

    [HumansTheory]
    [Xunit.InlineData("coordinators")]
    [Xunit.InlineData("browse")]
    [Xunit.InlineData("departments")]
    [Xunit.InlineData("admin-rotas")]
    [Xunit.InlineData("admin-staffing")]
    [Xunit.InlineData("tags")]
    [Xunit.InlineData("profile")]
    [Xunit.InlineData("shift")]
    public async Task BrowseReads_CanceledToken_ReachesColdSource(string boundary)
    {
        var (es, rota) = SeedRotaScenario(RotaPeriod.Event);
        SeedShift(rota, dayOffset: 1);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        _teamService.GetUserCoordinatedTeamIdsAsync(Arg.Any<Guid>(), aborted.Token)
            .Returns(Task.FromCanceled<IReadOnlyList<Guid>>(aborted.Token));

        Func<Task> read = boundary switch
        {
            "coordinators" => () => _service.GetCoordinatorTeamIdsAsync(Guid.NewGuid(), aborted.Token),
            "browse" => () => _service.GetBrowseShiftsAsync(new ShiftBrowseQuery(es.Id), aborted.Token),
            "admin-rotas" => () => _service.GetRotasByDepartmentAsync(rota.TeamId, es.Id, aborted.Token),
            "admin-staffing" => () => _service.GetStaffingSnapshotAsync(es.Id, ct: aborted.Token),
            "tags" => () => _service.GetTagsAsync(ct: aborted.Token),
            "profile" => () => _service.GetShiftProfileAsync(Guid.NewGuid(), aborted.Token),
            "shift" => () => _service.GetShiftByIdAsync(Guid.NewGuid(), aborted.Token),
            _ => () => _service.GetDepartmentsWithRotasAsync(es.Id, aborted.Token)
        };

        await read.Should().ThrowAsync<OperationCanceledException>();
    }

    [HumansFact]
    public async Task GetShiftByIdAsync_AnswersForAShiftWhoseEventHasNoCalendar()
    {
        // Settings knows no event at all: the ownership read must still find the shift,
        // or deleting/voluntelling an existing shift would 404.
        var (_, rota) = SeedRotaScenario(RotaPeriod.Event);
        var shift = SeedShift(rota, dayOffset: 2);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);
        var service = new ShiftManagementService(
            new ShiftRepository(ShiftsDbFactory, ShiftsDb, Clock), AuditLog, AdminAuthorization,
            new ServiceLocatorBuilder().With(_teamService).Build(), Cache,
            new ShiftViewProbe().Cache,
            new EventCalendarResolver(Substitute.For<Humans.Settings.Contracts.ISettingsService>(), Substitute.For<Humans.Shifts.Data.IShiftManagementRepository>()), Clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ShiftManagementService>.Instance);

        var found = await service.GetShiftByIdAsync(shift.Id, Xunit.TestContext.Current.CancellationToken);

        found.Should().NotBeNull();
        found!.Shift.Id.Should().Be(shift.Id);
        found.Shift.DayOffset.Should().Be(2);
        found.Rota.Id.Should().Be(rota.Id);
        found.Rota.TeamId.Should().Be(rota.TeamId);
    }

    [HumansFact]
    public async Task GetRotasByDepartmentAsync_CarriesEveryShiftAndSignup()
    {
        var (es, rota) = SeedRotaScenario(RotaPeriod.Event);
        var shift = SeedShift(rota, dayOffset: 1);
        SeedSignup(shift, SeedUser("Alice"), SignupStatus.Confirmed);
        SeedSignup(shift, SeedUser("Bob"), SignupStatus.Pending);
        SeedSignup(shift, SeedUser("Carol"), SignupStatus.Bailed);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var rows = await _service.GetRotasByDepartmentAsync(rota.TeamId, es.Id, Xunit.TestContext.Current.CancellationToken);

        var row = rows.Should().ContainSingle().Subject;
        row.Rota.Id.Should().Be(rota.Id);
        var shiftRow = row.Shifts.Should().ContainSingle().Subject;
        shiftRow.Shift.Id.Should().Be(shift.Id);
        shiftRow.ConfirmedCount.Should().Be(1);
        shiftRow.Signups.Select(s => s.Status).Should().BeEquivalentTo(
            [SignupStatus.Confirmed, SignupStatus.Pending, SignupStatus.Bailed]);
    }

    [HumansFact]
    public async Task UpdateShiftProfileAsync_CreatesTheRowOnceThenUpdatesIt()
    {
        var userId = Guid.NewGuid();

        await _service.UpdateShiftProfileAsync(new ShiftVolunteerProfileInfo(userId, ["Cooking"], [], ["English"]));
        await _service.UpdateShiftProfileAsync(new ShiftVolunteerProfileInfo(userId, ["Driving"], ["Early bird"], []));

        var saved = await ShiftsDb.VolunteerEventProfiles.AsNoTracking()
            .SingleAsync(p => p.UserId == userId, Xunit.TestContext.Current.CancellationToken);
        saved.Skills.Should().Equal("Driving");
        saved.Quirks.Should().Equal("Early bird");
        saved.Languages.Should().BeEmpty();
    }

    [HumansFact]
    public async Task GetBrowseShifts_IncludeSignups_ReturnsConfirmedAndPendingOnly()
    {
        var (es, rota) = SeedRotaScenario(RotaPeriod.Event);
        var shift = SeedShift(rota, dayOffset: 1);
        var confirmedUser = SeedUser("Alice");
        var pendingUser = SeedUser("Bob");
        var bailedUser = SeedUser("Charlie");

        SeedSignup(shift, confirmedUser, SignupStatus.Confirmed);
        SeedSignup(shift, pendingUser, SignupStatus.Pending);
        SeedSignup(shift, bailedUser, SignupStatus.Bailed);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var results = await _service.GetBrowseShiftsAsync(new ShiftBrowseQuery(
            es.Id,
            Flags: ShiftBrowseQueryFlags.IncludeSignups));

        results.Should().HaveCount(1);
        var signups = results[0].Signups;
        signups.Should().HaveCount(2);
        signups.Select(s => s.DisplayName).Should().BeEquivalentTo("Alice", "Bob");
    }

    [HumansFact]
    public async Task BrowseDisplayItem_OrdersConfirmedFirstThenNames()
    {
        var (es, rota) = SeedRotaScenario(RotaPeriod.Event);
        var shift = SeedShift(rota, dayOffset: 1);
        var confirmedUser = SeedUser("Zara");
        var pendingUser = SeedUser("Alice");

        var otherConfirmed = SeedUser("aaron");
        SeedSignup(shift, pendingUser, SignupStatus.Pending);
        SeedSignup(shift, confirmedUser, SignupStatus.Confirmed);
        SeedSignup(shift, otherConfirmed, SignupStatus.Confirmed);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var results = await _service.GetBrowseShiftsAsync(new ShiftBrowseQuery(
            es.Id,
            Flags: ShiftBrowseQueryFlags.IncludeSignups));

        var signups = ShiftBrowseMapper.MapToDisplayItem(results[0]).Signups;
        signups.Select(s => s.DisplayName).Should().Equal("aaron", "Zara", "Alice");
        signups.Select(s => s.Status).Should().Equal(
            SignupStatus.Confirmed, SignupStatus.Confirmed, SignupStatus.Pending);
    }

    [HumansFact]
    public async Task GetBrowseShifts_WithoutIncludeSignups_ReturnsEmptySignups()
    {
        var (es, rota) = SeedRotaScenario(RotaPeriod.Event);
        var shift = SeedShift(rota, dayOffset: 1);
        var user = SeedUser("Alice");
        SeedSignup(shift, user, SignupStatus.Confirmed);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var results = await _service.GetBrowseShiftsAsync(new ShiftBrowseQuery(es.Id));

        results.Should().HaveCount(1);
        results[0].Signups.Should().BeEmpty();
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
        TeamsDb.Teams.Add(unpromotedSubTeam);
        TeamsDb.Teams.Add(promotedSubTeam);

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
        ShiftsDb.Rotas.Add(unpromotedRota);
        ShiftsDb.Rotas.Add(promotedRota);

        SeedShift(parentRota, dayOffset: 1);
        SeedShift(unpromotedRota, dayOffset: 1);
        SeedShift(promotedRota, dayOffset: 1);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var results = await _service.GetBrowseShiftsAsync(new ShiftBrowseQuery(
            es.Id,
            DepartmentId: parentTeamId));

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
        TeamsDb.Teams.Add(promotedSubTeam);

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
        ShiftsDb.Rotas.Add(promotedRota);

        SeedShift(parentRota, dayOffset: 1);
        SeedShift(promotedRota, dayOffset: 1);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var results = await _service.GetBrowseShiftsAsync(new ShiftBrowseQuery(
            es.Id,
            DepartmentId: promotedSubTeam.Id));

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
        TeamsDb.Teams.Add(unpromotedSubTeam);
        TeamsDb.Teams.Add(promotedSubTeam);

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
        ShiftsDb.Rotas.Add(unpromotedRota);
        ShiftsDb.Rotas.Add(promotedRota);

        SeedShift(parentRota, dayOffset: 1);
        SeedShift(unpromotedRota, dayOffset: 1);
        SeedShift(promotedRota, dayOffset: 1);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var results = await _service.GetUrgentShiftsAsync(es.Id, departmentId: parentTeamId);

        results.Select(r => r.Shift.RotaId).Should().BeEquivalentTo([
            parentRota.Id,
            unpromotedRota.Id
        ]);
    }

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
        ShiftsDb.Shifts.Add(shift);
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
        ShiftsDb.ShiftSignups.Add(signup);
    }

    [HumansFact]
    public async Task SearchAsync_GuidQuery_ResolvesARotaHiddenFromVolunteers()
    {
        // A GUID-query hit skips the visibility filter on purpose: the destination
        // /Shifts?departmentId={teamId} still filters hidden rotas, so the hit is a link, not access.
        // The text-query half needs Postgres ILike and lives in Humans.Integration.Tests.
        var (_, rota) = SeedRotaScenario(RotaPeriod.Event);
        rota.IsVisibleToVolunteers = false;
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var results = await _service.SearchAsync(
            rota.Id.ToString(), int.MaxValue, Xunit.TestContext.Current.CancellationToken);

        var hit = results.Should().ContainSingle().Subject;
        hit.RotaId.Should().Be(rota.Id);
        hit.Name.Should().Be("Test Rota");
        hit.Score.Should().Be(StringSearchExtensions.ExactNameScore);
    }

    [HumansFact]
    public async Task SearchAsync_GuidQuery_ReturnsNothing_WhenNoRotaHasThatId()
    {
        SeedRotaScenario(RotaPeriod.Event);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var results = await _service.SearchAsync(
            Guid.NewGuid().ToString(), int.MaxValue, Xunit.TestContext.Current.CancellationToken);

        results.Should().BeEmpty();
    }

    [HumansFact]
    public async Task SearchAsync_GuidQuery_ObservesCallerCancellation()
    {
        var (_, rota) = SeedRotaScenario(RotaPeriod.Event);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);
        using var request = new CancellationTokenSource();
        await request.CancelAsync();

        Func<Task> action = () => _service.SearchAsync(rota.Id.ToString(), int.MaxValue, request.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    [HumansFact]
    public async Task SearchAsync_TextQuery_ReturnsNothing_WhenNoEventIsActive()
    {
        var results = await _service.SearchAsync(
            "rota", int.MaxValue, Xunit.TestContext.Current.CancellationToken);

        results.Should().BeEmpty();
    }

    [HumansFact]
    public async Task GetBrowseShifts_HiddenRota_IsAbsentForAVolunteer_AndPresentForAPrivilegedViewer()
    {
        // Destination-page half of the GUID search: /Shifts?departmentId={teamId} builds a default
        // ShiftBrowseQuery, which excludes hidden rotas; only privileged viewers add IncludeHidden.
        var (es, rota) = SeedRotaScenario(RotaPeriod.Event);
        rota.IsVisibleToVolunteers = false;
        SeedShift(rota, dayOffset: 1);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var volunteerView = await _service.GetBrowseShiftsAsync(
            new ShiftBrowseQuery(es.Id, rota.TeamId));
        var privilegedView = await _service.GetBrowseShiftsAsync(
            new ShiftBrowseQuery(es.Id, rota.TeamId, Flags: ShiftBrowseQueryFlags.IncludeHidden));

        volunteerView.Should().BeEmpty();
        privilegedView.Should().ContainSingle().Which.Shift.RotaId.Should().Be(rota.Id);
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
        ShiftsDb.EventSettings.Add(es);

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
        TeamsDb.Teams.Add(team);

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
        ShiftsDb.Rotas.Add(rota);

        rota.EventSettings = es;

        return (es, rota);
    }

    [HumansTheory]
    [Xunit.InlineData("coordinated-teams")]
    [Xunit.InlineData("team")]
    public async Task IsDeptCoordinatorAsync_Reads_HonourCallerCancellation(string boundary)
    {
        using var request = new CancellationTokenSource();
        await request.CancelAsync();
        var userId = Guid.NewGuid();
        var departmentId = Guid.NewGuid();
        _teamService.GetUserCoordinatedTeamIdsAsync(userId, Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                if (string.Equals(boundary, "coordinated-teams", StringComparison.Ordinal))
                    call.Arg<CancellationToken>().ThrowIfCancellationRequested();
                return Task.FromResult<IReadOnlyList<Guid>>([]);
            });
        _teamService.GetTeamAsync(departmentId, Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                if (string.Equals(boundary, "team", StringComparison.Ordinal))
                    call.Arg<CancellationToken>().ThrowIfCancellationRequested();
                return Task.FromResult<TeamInfo?>(null);
            });

        var read = () => _service.IsDeptCoordinatorAsync(userId, departmentId, request.Token);

        await read.Should().ThrowAsync<OperationCanceledException>();
    }

    [HumansFact]
    public async Task GetCoordinatorTeamIdsAsync_ReadsTeamsEveryCall_SoACoordinatorChangeShowsImmediately()
    {
        // No Shifts-side cache: Teams' in-RAM read model answers and Teams keeps it current
        // on its own writes, so no other section has to evict anything in Shifts.
        var userId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        _teamService.GetUserCoordinatedTeamIdsAsync(userId, Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<IReadOnlyList<Guid>>([teamId]),
                Task.FromResult<IReadOnlyList<Guid>>([]));

        (await _service.GetCoordinatorTeamIdsAsync(userId)).Should().Equal(teamId);
        (await _service.GetCoordinatorTeamIdsAsync(userId)).Should().BeEmpty();
    }

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
        _teamService.GetTeamAsync(subTeamBId, Arg.Any<CancellationToken>())
            .Returns(new TeamInfo(
                subTeamBId, "SubTeamB", null, "subteam-b",
                IsActive: true, IsSystemTeam: false, SystemTeamType.None, RequiresApproval: false,
                IsPublicPage: true, IsHidden: false, IsPromotedToDirectory: false,
                CreatedAt: TestNow, Members: [],
                ParentTeamId: departmentId));

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
        _teamService.GetTeamAsync(departmentId, Arg.Any<CancellationToken>())
            .Returns(new TeamInfo(
                departmentId, "Department", null, "department",
                IsActive: true, IsSystemTeam: false, SystemTeamType.None, RequiresApproval: false,
                IsPublicPage: true, IsHidden: false, IsPromotedToDirectory: false,
                CreatedAt: TestNow, Members: []));

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
        _teamService.GetTeamAsync(subTeamId, Arg.Any<CancellationToken>())
            .Returns(new TeamInfo(
                subTeamId, "SubTeam", null, "subteam",
                IsActive: true, IsSystemTeam: false, SystemTeamType.None, RequiresApproval: false,
                IsPublicPage: true, IsHidden: false, IsPromotedToDirectory: false,
                CreatedAt: TestNow, Members: [],
                ParentTeamId: departmentId));

        var result = await _service.IsDeptCoordinatorAsync(userId, subTeamId);

        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task GetOverallCoverageAsync_ReturnsCorrectFilledTotalRatio()
    {
        // Active event with 10 slots across 10 shifts, 7 confirmed signups.
        var (_, rota) = SeedRotaScenario(RotaPeriod.Event);

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
        ShiftsDb.Shifts.AddRange(shifts);

        // 7 confirmed signups on the first 7 shifts
        var userId = Guid.NewGuid();
        for (var i = 0; i < 7; i++)
        {
            ShiftsDb.ShiftSignups.Add(new ShiftSignup
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                ShiftId = shifts[i].Id,
                Status = SignupStatus.Confirmed,
                CreatedAt = TestNow,
                UpdatedAt = TestNow
            });
        }

        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var (filled, total, ratio) = await _service.GetOverallCoverageAsync(Xunit.TestContext.Current.CancellationToken);

        filled.Should().Be(7);
        total.Should().Be(10);
        ratio.Should().Be(0.7);
    }

    [HumansFact]
    public async Task GetOverallCoverageAsync_ReturnsZero_WhenNoActiveEvent()
    {
        // No event settings seeded → no active event.
        var (filled, total, ratio) = await _service.GetOverallCoverageAsync(Xunit.TestContext.Current.CancellationToken);

        filled.Should().Be(0);
        total.Should().Be(0);
        ratio.Should().Be(0d);
    }

    [HumansFact]
    public async Task GetKnobsAsync_NoRowYet_ReturnsNull()
    {
        (await _service.GetKnobsAsync(Guid.NewGuid())).Should().BeNull();
    }

    [HumansFact]
    public async Task GetKnobsAsync_AbortedReadPropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        Func<Task> read = () => _service.GetKnobsAsync(Guid.NewGuid(), cancellation.Token);

        await read.Should().ThrowAsync<OperationCanceledException>();
    }

    private static readonly Guid ActorId = Guid.NewGuid();

    [HumansFact]
    public async Task SaveKnobsAsync_NoRowYet_CreatesOneOnDemand()
    {
        var eventId = Guid.NewGuid();

        await _service.SaveKnobsAsync(eventId, isShiftBrowsingOpen: true, globalVolunteerCap: 50, reminderLeadTimeHours: 12,
            new BuildSubPeriodStarts(-28, -20, -10, -3), actorUserId: ActorId);

        ShiftsDb.ChangeTracker.Clear();
        var saved = await ShiftsDb.EventSettings.AsNoTracking().SingleAsync(e => e.Id == eventId, Xunit.TestContext.Current.CancellationToken);
        saved.IsShiftBrowsingOpen.Should().BeTrue();
        saved.GlobalVolunteerCap.Should().Be(50);
        saved.ReminderLeadTimeHours.Should().Be(12);
        saved.SubPeriodFirstCrewStart.Should().Be(-28);
        saved.SubPeriodSetupWeekStart.Should().Be(-20);
        saved.SubPeriodPreEventWeekStart.Should().Be(-10);
        saved.SubPeriodFinishingWeekendStart.Should().Be(-3);
        await AuditLog.Received(1).LogAsync(
            ShiftsAuditActions.ShiftSettingsUpdated, nameof(EventSettings), eventId,
            Arg.Is<string>(d => d.Contains("first crew -28") && d.Contains("finishing weekend -3")),
            ActorId, Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task SaveKnobsAsync_RowAlreadyExists_UpdatesItInPlace()
    {
        var existing = new EventSettings
        {
            Id = Guid.NewGuid(),
            IsShiftBrowsingOpen = false,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        };
        ShiftsDb.EventSettings.Add(existing);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        await _service.SaveKnobsAsync(existing.Id, isShiftBrowsingOpen: true, globalVolunteerCap: null, reminderLeadTimeHours: 24,
            new BuildSubPeriodStarts(-25, -16, -9, -4), actorUserId: ActorId);

        (await ShiftsDb.EventSettings.CountAsync(Xunit.TestContext.Current.CancellationToken)).Should().Be(1);
    }

    // Build sub-periods moved from Settings to Shifts: unset ones fall back to Settings until the copy runs.

    private static Humans.Settings.Contracts.EventSettingsInfo SettingsCycle(Guid id, string name, int firstCrew) => new(
        Id: id, EventName: name, Year: 2026, TimeZoneId: "Europe/Madrid", GateOpeningDate: new LocalDate(2026, 7, 9),
        BuildStartOffset: -30, EventEndOffset: 6, StrikeEndOffset: 9,
        FirstCrewStartOffset: firstCrew, SetupWeekStartOffset: -20, PreEventWeekStartOffset: -10, FinishingWeekendStartOffset: -3,
        EarlyEntryCapacity: new Dictionary<int, int>(), BarriosEarlyEntryAllocation: null, EarlyEntryClose: null,
        Status: Humans.Settings.Contracts.EventSettingsStatus.Inactive);

    [HumansFact]
    public async Task GetUncopiedBuildSubPeriodsAsync_ListsEveryCycleStillLeaningOnSettings()
    {
        var unsetRow = new EventSettings { Id = Guid.NewGuid(), CreatedAt = TestNow, UpdatedAt = TestNow };
        var copiedRow = new EventSettings
        {
            Id = Guid.NewGuid(),
            SubPeriodFirstCrewStart = -30,
            SubPeriodSetupWeekStart = -21,
            SubPeriodPreEventWeekStart = -11,
            SubPeriodFinishingWeekendStart = -2,
            CreatedAt = TestNow,
            UpdatedAt = TestNow,
        };
        ShiftsDb.EventSettings.AddRange(unsetRow, copiedRow);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);
        var noShiftsRow = Guid.NewGuid();
        _settingsService.GetAllEventSettingsAsync(Arg.Any<CancellationToken>()).Returns(
        [
            SettingsCycle(unsetRow.Id, "Nowhere 2026", -28),
            SettingsCycle(copiedRow.Id, "Nowhere 2025", -25),
            SettingsCycle(noShiftsRow, "Nowhere 2027", -27),
        ]);

        var pending = await _service.GetUncopiedBuildSubPeriodsAsync(Xunit.TestContext.Current.CancellationToken);

        pending.Should().BeEquivalentTo(new[]
        {
            (unsetRow.Id, "Nowhere 2026", new BuildSubPeriodStarts(-28, -20, -10, -3)),
            (noShiftsRow, "Nowhere 2027", new BuildSubPeriodStarts(-27, -20, -10, -3)),
        });
    }

    [HumansFact]
    public async Task CopyBuildSubPeriodsAsync_WritesSettingsValuesOntoShiftsRowsCreatingMissingOnes()
    {
        var unsetRow = new EventSettings { Id = Guid.NewGuid(), CreatedAt = TestNow, UpdatedAt = TestNow };
        ShiftsDb.EventSettings.Add(unsetRow);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);
        var noShiftsRow = Guid.NewGuid();
        _settingsService.GetAllEventSettingsAsync(Arg.Any<CancellationToken>()).Returns(
        [
            SettingsCycle(unsetRow.Id, "Nowhere 2026", -28),
            SettingsCycle(noShiftsRow, "Nowhere 2027", -27),
        ]);

        (await _service.CopyBuildSubPeriodsAsync(Xunit.TestContext.Current.CancellationToken)).Should().Be(2);

        ShiftsDb.ChangeTracker.Clear();
        var saved = await ShiftsDb.EventSettings.AsNoTracking().SingleAsync(e => e.Id == unsetRow.Id, Xunit.TestContext.Current.CancellationToken);
        saved.SubPeriodFirstCrewStart.Should().Be(-28);
        saved.SubPeriodSetupWeekStart.Should().Be(-20);
        saved.SubPeriodPreEventWeekStart.Should().Be(-10);
        saved.SubPeriodFinishingWeekendStart.Should().Be(-3);
        var created = await ShiftsDb.EventSettings.AsNoTracking().SingleAsync(e => e.Id == noShiftsRow, Xunit.TestContext.Current.CancellationToken);
        created.SubPeriodFirstCrewStart.Should().Be(-27);
        created.IsShiftBrowsingOpen.Should().BeFalse();
        (await _service.GetUncopiedBuildSubPeriodsAsync(Xunit.TestContext.Current.CancellationToken)).Should().BeEmpty();
    }

    [HumansFact]
    public async Task DeleteRotaAsync_WithConfirmedSignup_Refuses()
    {
        var (_, rota) = SeedRotaScenario(RotaPeriod.Event);
        var shift = SeedShift(rota, dayOffset: 1);
        var user = SeedUser("Alice");
        SeedSignup(shift, user, SignupStatus.Confirmed);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var result = await _service.DeleteRotaAsync(rota.Id, Guid.NewGuid());
        result.ErrorKey.Should().Be("Shifts_Delete_ConfirmedSignups");
        result.ConfirmedSignups.Should().Be(1);
        (await ShiftsDb.Rotas.FindAsync(rota.Id)).Should().NotBeNull();
    }

    [HumansFact]
    public async Task DeleteRotaAsync_WithOnlyPendingSignups_DeletesThemWithTheRota()
    {
        var (_, rota) = SeedRotaScenario(RotaPeriod.Event);
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
        await ShiftsDb.ShiftSignups.AddRangeAsync(pending1, pending2);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var actorUserId = Guid.NewGuid();
        await _service.DeleteRotaAsync(rota.Id, actorUserId);

        // AsNoTracking: the test's context still tracks the seeded rota and signups.
        (await ShiftsDb.Rotas.AsNoTracking()
            .AnyAsync(r => r.Id == rota.Id, Xunit.TestContext.Current.CancellationToken))
            .Should().BeFalse();
        (await ShiftsDb.ShiftSignups
            .AsNoTracking()
            .Where(s => s.Id == pending1.Id || s.Id == pending2.Id)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken))
            .Should().BeEmpty();
        foreach (var removed in new[] { pending1, pending2 })
        {
            await AuditLog.Received(1).LogAsync(ShiftsAuditActions.ShiftSignupCancelled, nameof(ShiftSignup), removed.Id,
                Arg.Is<string>(text => text.Contains(rota.Name, StringComparison.Ordinal)
                    && text.Contains("deleted", StringComparison.Ordinal)),
                actorUserId, removed.UserId, "User");
        }
    }

    [HumansFact]
    public async Task MoveRotaToTeamAsync_WritesRotaMovedToTeamAuditEntry()
    {
        var (_, rota) = SeedRotaScenario(RotaPeriod.Build);
        SeedSignup(SeedShift(rota, -4), SeedUser("Builder"), SignupStatus.Confirmed);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var targetTeamId = Guid.NewGuid();
        var actorUserId = Guid.NewGuid();

        var sourceTeam = await TeamsDb.Teams.FirstAsync(t => t.Id == rota.TeamId, Xunit.TestContext.Current.CancellationToken);
        _teamService.GetTeamAsync(rota.TeamId, Arg.Any<CancellationToken>())
            .Returns(ToTeamInfo(sourceTeam));
        _teamService.GetTeamAsync(targetTeamId, Arg.Any<CancellationToken>())
            .Returns(new TeamInfo(
                targetTeamId, "Target Department", null, "target-dept",
                IsActive: true, IsSystemTeam: false, SystemTeamType.None, RequiresApproval: false,
                IsPublicPage: true, IsHidden: false, IsPromotedToDirectory: false,
                CreatedAt: TestNow, Members: []));

        var result = await _service.MoveRotaToTeamAsync(new MoveRotaInput(
            rota.Id,
            rota.TeamId,
            targetTeamId,
            actorUserId));

        result.Succeeded.Should().BeTrue();
        result.Message.Should().Be("Rota 'Test Rota' moved to Target Department.");
        result.RedirectSlug.Should().Be("target-dept");

        await AuditLog.Received(1).LogAsync(
            ShiftsAuditActions.RotaMovedToTeam,
            nameof(Rota),
            rota.Id,
            Arg.Any<string>(),
            actorUserId,
            targetTeamId,
            nameof(Team));
    }

    [HumansFact]
    public async Task GetActivePendingShiftSignupCountsByTeam_returns_empty_dict_when_no_active_event()
    {
        // No EventSettings seeded in ShiftsDb → NewCalendarResolver() resolves no active event.
        var repo = Substitute.For<IShiftManagementRepository>();
        var service = BuildServiceWithRepo(repo);

        var result = await service.GetActivePendingShiftSignupCountsByTeamAsync(Xunit.TestContext.Current.CancellationToken);

        result.Should().BeEmpty();
        await repo.DidNotReceiveWithAnyArgs()
            .GetPendingSignupCountsByTeamAsync(Guid.Empty, null, null, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetActivePendingShiftSignupCountsByTeam_delegates_to_repo_when_active_event_exists()
    {
        var es = new EventSettings
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
        ShiftsDb.EventSettings.Add(es);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);
        var expected = new Dictionary<Guid, int> { [Guid.NewGuid()] = 3 };

        var repo = Substitute.For<IShiftManagementRepository>();
        repo.GetPendingSignupCountsByTeamAsync(es.Id, null, null, Arg.Any<CancellationToken>())
            .Returns(expected);
        var service = BuildServiceWithRepo(repo);

        var result = await service.GetActivePendingShiftSignupCountsByTeamAsync(Xunit.TestContext.Current.CancellationToken);

        result.Should().BeEquivalentTo(expected);
    }

    private ShiftManagementService BuildServiceWithRepo(IShiftManagementRepository repo) =>
        new(
            repo,
            AuditLog,
            AdminAuthorization,
            new ServiceLocatorBuilder().Build(),
            Cache,
            new ShiftViewProbe().Cache,
            NewCalendarResolver(),
            Clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<ShiftManagementService>.Instance);
}
