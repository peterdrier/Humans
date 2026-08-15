using Humans.Shifts.Domain;
using Humans.Auth.Contracts;
using Humans.Teams.Data;
using Humans.Teams.Domain;
using AwesomeAssertions;
using Humans.Shifts.Services;
using Humans.Shifts.Contracts;
using Humans.Teams.Contracts;
using Humans.Application.Interfaces.Users;
using Humans.Shifts.Tests.Infrastructure;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Shifts.Data;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NSubstitute;
using Xunit;
using Humans.Users.Contracts;

namespace Humans.Shifts.Tests.Services;

/// <summary>
/// Guard clauses and boundary arithmetic on the ShiftManagementService write paths:
/// rota create/move, shift create/update/delete, and the two bulk-generation entry points.
/// </summary>
public sealed class ShiftManagementWriteGuardTests : ShiftsTestHarness
{
    private static readonly Instant TestNow = Instant.FromUtc(2026, 6, 15, 12, 0);

    private readonly ITeamServiceRead _teamService;
    private readonly IShiftViewInvalidator _viewInvalidator;
    private readonly IRoleAssignmentService _roleAssignments;
    private readonly ShiftManagementService _service;

    public ShiftManagementWriteGuardTests()
        : base(TestNow)
    {
        _teamService = Substitute.For<ITeamServiceRead>();
        _viewInvalidator = Substitute.For<IShiftViewInvalidator>();
        _roleAssignments = Substitute.For<IRoleAssignmentService>();
        var userService = Substitute.For<IUserService>();
        userService.StubGetUserInfosFromContext(Db);

        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyDictionary<Guid, TeamInfo>>(
                TeamsDb.Teams.AsEnumerable().ToDictionary(t => t.Id, ToTeamInfo)));
        _teamService.GetTeamAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var id = ci.Arg<Guid>();
                var team = TeamsDb.Teams.AsEnumerable().FirstOrDefault(t => t.Id == id);
                return Task.FromResult(team is null ? null : ToTeamInfo(team));
            });

        var serviceProvider = new ServiceLocatorBuilder()
            .With(_teamService)
            .With(userService)
            .With<IUserServiceRead>(userService)
            .With(_roleAssignments)
            .Build();

        _service = new ShiftManagementService(
            new ShiftRepository(ShiftsDbFactory, ShiftsDb, Clock),
            AuditLog,
            AdminAuthorization,
            serviceProvider,
            Cache,
            _viewInvalidator,
            Clock);
    }

    // ============================================================
    // CanApproveSignupsAsync — system-wide roles vs department scope
    // ============================================================

    [HumansTheory]
    [InlineData(RoleNames.Admin)]
    [InlineData(RoleNames.NoInfoAdmin)]
    [InlineData(RoleNames.VolunteerCoordinator)]
    public async Task CanApproveSignups_IsGrantedByAnyOneSystemWideRoleOnItsOwn(string roleName)
    {
        var userId = Guid.NewGuid();
        _roleAssignments.HasActiveRoleAsync(userId, roleName).Returns(true);

        (await _service.CanApproveSignupsAsync(userId, Guid.NewGuid())).Should().BeTrue();
    }

    [HumansFact]
    public async Task CanApproveSignups_IsRefused_ForANonCoordinatorWithoutASystemWideRole()
    {
        var userId = Guid.NewGuid();
        _teamService.GetUserCoordinatedTeamIdsAsync(userId, Arg.Any<CancellationToken>())
            .Returns([]);

        (await _service.CanApproveSignupsAsync(userId, Guid.NewGuid())).Should().BeFalse();
    }

    [HumansFact]
    public async Task CanApproveSignups_FallsBackToDepartmentCoordinatorScope()
    {
        var userId = Guid.NewGuid();
        var department = SeedDepartment("Gate");
        var other = SeedDepartment("Sanctuary");
        await SaveAllAsync(Ct);
        _teamService.GetUserCoordinatedTeamIdsAsync(userId, Arg.Any<CancellationToken>())
            .Returns([department.Id]);

        (await _service.CanApproveSignupsAsync(userId, department.Id)).Should().BeTrue();
        (await _service.CanApproveSignupsAsync(userId, other.Id)).Should().BeFalse();
    }

    // ============================================================
    // CreateRotaAsync — target-team and event validation
    // ============================================================

    [HumansFact]
    public async Task CreateRotaAsync_Throws_WhenTeamNotFound()
    {
        var es = SeedEventSettings();
        await SaveAllAsync(Ct);

        var act = () => _service.CreateRotaAsync(NewRota(es.Id, Guid.NewGuid()));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Team not found.");
        ShiftsDb.Rotas.Should().BeEmpty();
    }

    [HumansFact]
    public async Task CreateRotaAsync_Throws_WhenTeamIsSystemTeam()
    {
        var es = SeedEventSettings();
        var team = SeedDepartment("Volunteers", systemTeamType: SystemTeamType.Volunteers);
        await SaveAllAsync(Ct);

        var act = () => _service.CreateRotaAsync(NewRota(es.Id, team.Id));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Rotas cannot be created on system teams.");
        ShiftsDb.Rotas.Should().BeEmpty();
    }

    [HumansFact]
    public async Task CreateRotaAsync_Throws_WhenEventSettingsMissing()
    {
        var team = SeedDepartment("Gate");
        await SaveAllAsync(Ct);

        var act = () => _service.CreateRotaAsync(NewRota(Guid.NewGuid(), team.Id));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Active EventSettings not found.");
        ShiftsDb.Rotas.Should().BeEmpty();
    }

    [HumansFact]
    public async Task CreateRotaAsync_Throws_WhenEventSettingsInactive()
    {
        var es = SeedEventSettings(isActive: false);
        var team = SeedDepartment("Gate");
        await SaveAllAsync(Ct);

        var act = () => _service.CreateRotaAsync(NewRota(es.Id, team.Id));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Active EventSettings not found.");
        ShiftsDb.Rotas.Should().BeEmpty();
    }

    [HumansFact]
    public async Task CreateRotaAsync_WithTags_PersistsRotaAndTags_AndInvalidatesRotaView()
    {
        var es = SeedEventSettings();
        var team = SeedDepartment("Gate");
        var tag = SeedTag("Heavy lifting");
        await SaveAllAsync(Ct);

        var rota = NewRota(es.Id, team.Id);
        await _service.CreateRotaAsync(rota, [tag.Id]);

        var saved = await ShiftsDb.Rotas.AsNoTracking().Include(r => r.Tags)
            .SingleAsync(r => r.Id == rota.Id, Ct);
        saved.TeamId.Should().Be(team.Id);
        saved.UpdatedAt.Should().Be(TestNow);
        saved.Tags.Select(t => t.Id).Should().BeEquivalentTo([tag.Id]);
        _viewInvalidator.Received(1).InvalidateRota(rota.Id);
    }

    [HumansFact]
    public async Task CreateRotaAsync_WithEmptyTagList_LeavesRotaUntagged()
    {
        var es = SeedEventSettings();
        var team = SeedDepartment("Gate");
        SeedTag("Heavy lifting");
        await SaveAllAsync(Ct);

        var rota = NewRota(es.Id, team.Id);
        await _service.CreateRotaAsync(rota, []);

        var saved = await ShiftsDb.Rotas.AsNoTracking().Include(r => r.Tags)
            .SingleAsync(r => r.Id == rota.Id, Ct);
        saved.Tags.Should().BeEmpty();
    }

    // ============================================================
    // UpdateRotaAsync — null tag list means "leave tags alone"
    // ============================================================

    [HumansFact]
    public async Task UpdateRotaAsync_WithNullTagIds_LeavesExistingTags()
    {
        var (_, rota, _) = SeedRotaScenario(RotaPeriod.Event);
        var tag = SeedTag("Heavy lifting");
        await SaveAllAsync(Ct);
        await _service.UpdateRotaAsync(rota, [tag.Id]);

        rota.Name = "Renamed";
        await _service.UpdateRotaAsync(rota);

        var saved = await ShiftsDb.Rotas.AsNoTracking().Include(r => r.Tags)
            .SingleAsync(r => r.Id == rota.Id, Ct);
        saved.Name.Should().Be("Renamed");
        saved.Tags.Select(t => t.Id).Should().BeEquivalentTo([tag.Id]);
    }

    [HumansFact]
    public async Task UpdateRotaAsync_WithEmptyTagIds_ClearsExistingTags()
    {
        var (_, rota, _) = SeedRotaScenario(RotaPeriod.Event);
        var tag = SeedTag("Heavy lifting");
        await SaveAllAsync(Ct);
        await _service.UpdateRotaAsync(rota, [tag.Id]);

        await _service.UpdateRotaAsync(rota, []);

        var saved = await ShiftsDb.Rotas.AsNoTracking().Include(r => r.Tags)
            .SingleAsync(r => r.Id == rota.Id, Ct);
        saved.Tags.Should().BeEmpty();
    }

    // ============================================================
    // MoveRotaToTeamAsync — the five rejection paths
    // ============================================================

    [HumansFact]
    public async Task MoveRotaToTeamAsync_Fails_WhenRotaMissing()
    {
        var target = SeedDepartment("Target");
        await SaveAllAsync(Ct);

        var result = await _service.MoveRotaToTeamAsync(
            new MoveRotaInput(Guid.NewGuid(), Guid.NewGuid(), target.Id, Guid.NewGuid()));

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Be("Rota not found.");
    }

    [HumansFact]
    public async Task MoveRotaToTeamAsync_Fails_WhenRotaBelongsToADifferentSourceTeam()
    {
        var (_, rota, _) = SeedRotaScenario(RotaPeriod.Event);
        var target = SeedDepartment("Target");
        await SaveAllAsync(Ct);

        var result = await _service.MoveRotaToTeamAsync(
            new MoveRotaInput(rota.Id, Guid.NewGuid(), target.Id, Guid.NewGuid()));

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Be("Rota not found.");
        (await ShiftsDb.Rotas.AsNoTracking().SingleAsync(r => r.Id == rota.Id, Ct))
            .TeamId.Should().Be(rota.TeamId);
    }

    [HumansFact]
    public async Task MoveRotaToTeamAsync_Fails_WhenTargetTeamMissing()
    {
        var (_, rota, _) = SeedRotaScenario(RotaPeriod.Event);
        await SaveAllAsync(Ct);

        var result = await _service.MoveRotaToTeamAsync(
            new MoveRotaInput(rota.Id, rota.TeamId, Guid.NewGuid(), Guid.NewGuid()));

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Be("Target team not found.");
    }

    [HumansFact]
    public async Task MoveRotaToTeamAsync_Fails_WhenTargetIsASubTeam()
    {
        var (_, rota, department) = SeedRotaScenario(RotaPeriod.Event);
        var subTeam = SeedDepartment("Sub", parentTeamId: department.Id);
        await SaveAllAsync(Ct);

        var result = await _service.MoveRotaToTeamAsync(
            new MoveRotaInput(rota.Id, rota.TeamId, subTeam.Id, Guid.NewGuid()));

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Be("Rotas can only be moved to parent teams (departments).");
    }

    [HumansFact]
    public async Task MoveRotaToTeamAsync_Fails_WhenTargetIsASystemTeam()
    {
        var (_, rota, _) = SeedRotaScenario(RotaPeriod.Event);
        var systemTeam = SeedDepartment("Volunteers", systemTeamType: SystemTeamType.Volunteers);
        await SaveAllAsync(Ct);

        var result = await _service.MoveRotaToTeamAsync(
            new MoveRotaInput(rota.Id, rota.TeamId, systemTeam.Id, Guid.NewGuid()));

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Be("Rotas cannot be moved to system teams.");
    }

    [HumansFact]
    public async Task MoveRotaToTeamAsync_Fails_WhenRotaIsAlreadyOnTheTargetTeam()
    {
        var (_, rota, department) = SeedRotaScenario(RotaPeriod.Event);
        await SaveAllAsync(Ct);

        var result = await _service.MoveRotaToTeamAsync(
            new MoveRotaInput(rota.Id, rota.TeamId, department.Id, Guid.NewGuid()));

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Be("Rota is already in this team.");
        await AuditLog.DidNotReceive().LogAsync(
            AuditAction.RotaMovedToTeam, Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(),
            Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task MoveRotaToTeamAsync_RepointsRotaAndInvalidatesRotaView()
    {
        var (_, rota, _) = SeedRotaScenario(RotaPeriod.Event);
        var target = SeedDepartment("Target");
        await SaveAllAsync(Ct);

        var result = await _service.MoveRotaToTeamAsync(
            new MoveRotaInput(rota.Id, rota.TeamId, target.Id, Guid.NewGuid()));

        result.Succeeded.Should().BeTrue();
        result.RedirectSlug.Should().Be(target.Slug);
        ShiftsDb.ChangeTracker.Clear();
        var moved = await ShiftsDb.Rotas.AsNoTracking().SingleAsync(r => r.Id == rota.Id, Ct);
        moved.TeamId.Should().Be(target.Id);
        moved.UpdatedAt.Should().Be(TestNow);
        _viewInvalidator.Received(1).InvalidateRota(rota.Id);
    }

    // ============================================================
    // DeleteShiftAsync
    // ============================================================

    [HumansFact]
    public async Task DeleteShiftAsync_Throws_WhenShiftMissing()
    {
        var act = () => _service.DeleteShiftAsync(Guid.NewGuid());

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Shift not found.");
    }

    [HumansFact]
    public async Task DeleteShiftAsync_Throws_WhenAnySignupIsConfirmed()
    {
        var (_, rota, _) = SeedRotaScenario(RotaPeriod.Event);
        var shift = SeedShift(rota, dayOffset: 1);
        SeedSignup(shift, SeedUser("Alice").Id, SignupStatus.Confirmed);
        await SaveAllAsync(Ct);

        var act = () => _service.DeleteShiftAsync(shift.Id);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*1 humans have confirmed*");
        (await ShiftsDb.Shifts.AsNoTracking().AnyAsync(s => s.Id == shift.Id, Ct)).Should().BeTrue();
    }

    [HumansFact]
    public async Task DeleteShiftAsync_DeletesShiftAndPendingSignups_AndInvalidatesEveryAffectedView()
    {
        var (_, rota, _) = SeedRotaScenario(RotaPeriod.Event);
        var shift = SeedShift(rota, dayOffset: 1);
        var alice = SeedUser("Alice");
        var bob = SeedUser("Bob");
        SeedSignup(shift, alice.Id, SignupStatus.Pending);
        SeedSignup(shift, bob.Id, SignupStatus.Bailed);
        await SaveAllAsync(Ct);

        await _service.DeleteShiftAsync(shift.Id);

        ShiftsDb.ChangeTracker.Clear();
        (await ShiftsDb.Shifts.AsNoTracking().AnyAsync(s => s.Id == shift.Id, Ct)).Should().BeFalse();
        (await ShiftsDb.ShiftSignups.AsNoTracking().AnyAsync(s => s.ShiftId == shift.Id, Ct)).Should().BeFalse();
        _viewInvalidator.Received(1).InvalidateShift(shift.Id);
        _viewInvalidator.Received(1).InvalidateRota(rota.Id);
        _viewInvalidator.Received(1).InvalidateUser(alice.Id);
        _viewInvalidator.Received(1).InvalidateUser(bob.Id);
    }

    // ============================================================
    // CreateBuildStrikeShiftsAsync — day-offset window + staffing grid
    // ============================================================

    [HumansFact]
    public async Task CreateBuildStrikeShifts_Fails_WhenRotaBelongsToAnotherTeam()
    {
        var (_, rota, _) = SeedRotaScenario(RotaPeriod.Build);
        await SaveAllAsync(Ct);

        var result = await _service.CreateBuildStrikeShiftsAsync(
            new ConfigureBuildStrikeStaffingInput(rota.Id, Guid.NewGuid(), [new DayStaffingInput(-3, 1, 2)]));

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Be("Rota not found.");
        ShiftsDb.Shifts.Should().BeEmpty();
    }

    [HumansFact]
    public async Task CreateBuildStrikeShifts_Fails_WhenNoStaffingDaysSupplied()
    {
        var (_, rota, _) = SeedRotaScenario(RotaPeriod.Build);
        await SaveAllAsync(Ct);

        var result = await _service.CreateBuildStrikeShiftsAsync(
            new ConfigureBuildStrikeStaffingInput(rota.Id, rota.TeamId, []));

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Be("At least one staffing day is required.");
    }

    [HumansFact]
    public async Task CreateBuildStrikeShifts_Fails_WhenAnyDayHasMinAboveMax()
    {
        var (_, rota, _) = SeedRotaScenario(RotaPeriod.Build);
        await SaveAllAsync(Ct);

        var result = await _service.CreateBuildStrikeShiftsAsync(
            new ConfigureBuildStrikeStaffingInput(rota.Id, rota.TeamId,
            [
                new DayStaffingInput(-3, 1, 2),
                new DayStaffingInput(-2, 5, 4)
            ]));

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Be("MinVolunteers cannot exceed MaxVolunteers.");
        ShiftsDb.Shifts.Should().BeEmpty();
    }

    [HumansFact]
    public async Task CreateBuildStrikeShifts_Succeeds_WhenMinEqualsMax()
    {
        var (_, rota, _) = SeedRotaScenario(RotaPeriod.Build);
        await SaveAllAsync(Ct);

        var result = await _service.CreateBuildStrikeShiftsAsync(
            new ConfigureBuildStrikeStaffingInput(rota.Id, rota.TeamId, [new DayStaffingInput(-3, 4, 4)]));

        result.Succeeded.Should().BeTrue();
        result.CreatedCount.Should().Be(1);
    }

    [HumansTheory]
    [InlineData(-15)]
    [InlineData(0)]
    public async Task CreateBuildStrikeShifts_Throws_WhenBuildDayOffsetIsOutsideTheBuildWindow(int dayOffset)
    {
        var (_, rota, _) = SeedRotaScenario(RotaPeriod.Build);
        await SaveAllAsync(Ct);

        var act = () => _service.CreateBuildStrikeShiftsAsync(
            new ConfigureBuildStrikeStaffingInput(rota.Id, rota.TeamId, [new DayStaffingInput(dayOffset, 1, 2)]));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*outside the build period*");
        ShiftsDb.Shifts.Should().BeEmpty();
    }

    [HumansTheory]
    [InlineData(-14)]
    [InlineData(-1)]
    public async Task CreateBuildStrikeShifts_Accepts_BuildWindowEdges(int dayOffset)
    {
        var (_, rota, _) = SeedRotaScenario(RotaPeriod.Build);
        await SaveAllAsync(Ct);

        var result = await _service.CreateBuildStrikeShiftsAsync(
            new ConfigureBuildStrikeStaffingInput(rota.Id, rota.TeamId, [new DayStaffingInput(dayOffset, 1, 2)]));

        result.Succeeded.Should().BeTrue();
        (await ShiftsDb.Shifts.AsNoTracking().SingleAsync(Ct)).DayOffset.Should().Be(dayOffset);
    }

    [HumansTheory]
    [InlineData(6)]
    [InlineData(10)]
    public async Task CreateBuildStrikeShifts_Throws_WhenStrikeDayOffsetIsOutsideTheStrikeWindow(int dayOffset)
    {
        var (_, rota, _) = SeedRotaScenario(RotaPeriod.Strike);
        await SaveAllAsync(Ct);

        var act = () => _service.CreateBuildStrikeShiftsAsync(
            new ConfigureBuildStrikeStaffingInput(rota.Id, rota.TeamId, [new DayStaffingInput(dayOffset, 1, 2)]));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*outside the strike period*");
    }

    [HumansTheory]
    [InlineData(7)]
    [InlineData(9)]
    public async Task CreateBuildStrikeShifts_Accepts_StrikeWindowEdges(int dayOffset)
    {
        var (_, rota, _) = SeedRotaScenario(RotaPeriod.Strike);
        await SaveAllAsync(Ct);

        var result = await _service.CreateBuildStrikeShiftsAsync(
            new ConfigureBuildStrikeStaffingInput(rota.Id, rota.TeamId, [new DayStaffingInput(dayOffset, 1, 2)]));

        result.Succeeded.Should().BeTrue();
        (await ShiftsDb.Shifts.AsNoTracking().SingleAsync(Ct)).DayOffset.Should().Be(dayOffset);
    }

    [HumansFact]
    public async Task CreateBuildStrikeShifts_IsAdditive_SkippingDaysThatAlreadyHaveShifts()
    {
        var (_, rota, _) = SeedRotaScenario(RotaPeriod.Build);
        var existing = SeedShift(rota, dayOffset: -3);
        await SaveAllAsync(Ct);

        var result = await _service.CreateBuildStrikeShiftsAsync(
            new ConfigureBuildStrikeStaffingInput(rota.Id, rota.TeamId,
            [
                new DayStaffingInput(-3, 9, 9),
                new DayStaffingInput(-2, 1, 2)
            ]));

        result.Succeeded.Should().BeTrue();
        result.CreatedCount.Should().Be(1);
        ShiftsDb.ChangeTracker.Clear();
        var shifts = await ShiftsDb.Shifts.AsNoTracking().OrderBy(s => s.DayOffset).ToListAsync(Ct);
        shifts.Select(s => s.DayOffset).Should().Equal(-3, -2);
        shifts.Single(s => s.DayOffset == -3).MaxVolunteers.Should().Be(existing.MaxVolunteers);
    }

    [HumansFact]
    public async Task CreateBuildStrikeShifts_LastEntryWins_WhenADayIsListedTwice()
    {
        var (_, rota, _) = SeedRotaScenario(RotaPeriod.Build);
        await SaveAllAsync(Ct);

        var result = await _service.CreateBuildStrikeShiftsAsync(
            new ConfigureBuildStrikeStaffingInput(rota.Id, rota.TeamId,
            [
                new DayStaffingInput(-3, 1, 2),
                new DayStaffingInput(-3, 7, 8)
            ]));

        result.Succeeded.Should().BeTrue();
        var shift = await ShiftsDb.Shifts.AsNoTracking().SingleAsync(Ct);
        shift.MinVolunteers.Should().Be(7);
        shift.MaxVolunteers.Should().Be(8);
    }

    // ============================================================
    // GenerateEventShiftsAsync — event window + cartesian product
    // ============================================================

    [HumansTheory]
    [InlineData(-1, 3)]
    [InlineData(0, 7)]
    [InlineData(4, 2)]
    public async Task GenerateEventShifts_Fails_WhenTheDayRangeLeavesTheEventWindow(int start, int end)
    {
        var (_, rota, _) = SeedRotaScenario(RotaPeriod.Event);
        await SaveAllAsync(Ct);

        var result = await _service.GenerateEventShiftsAsync(new GenerateEventShiftsInput(
            rota.Id, rota.TeamId, start, end, [new ShiftTimeSlotInput(new LocalTime(8, 0), 4)], 1, 2));

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Be("Shift dates must fall within the event period.");
        ShiftsDb.Shifts.Should().BeEmpty();
    }

    [HumansFact]
    public async Task GenerateEventShifts_Accepts_ASingleDayAtTheEndOfTheEventWindow()
    {
        var (_, rota, _) = SeedRotaScenario(RotaPeriod.Event);
        await SaveAllAsync(Ct);

        var result = await _service.GenerateEventShiftsAsync(new GenerateEventShiftsInput(
            rota.Id, rota.TeamId, 6, 6, [new ShiftTimeSlotInput(new LocalTime(8, 0), 4)], 1, 2));

        result.Succeeded.Should().BeTrue();
        result.CreatedCount.Should().Be(1);
        (await ShiftsDb.Shifts.AsNoTracking().SingleAsync(Ct)).DayOffset.Should().Be(6);
    }

    [HumansFact]
    public async Task GenerateEventShifts_Fails_WhenNoTimeSlotsSupplied()
    {
        var (_, rota, _) = SeedRotaScenario(RotaPeriod.Event);
        await SaveAllAsync(Ct);

        var result = await _service.GenerateEventShiftsAsync(new GenerateEventShiftsInput(
            rota.Id, rota.TeamId, 0, 1, [], 1, 2));

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Be("At least one time slot is required.");
    }

    [HumansFact]
    public async Task GenerateEventShifts_Fails_WhenMinExceedsMax()
    {
        var (_, rota, _) = SeedRotaScenario(RotaPeriod.Event);
        await SaveAllAsync(Ct);

        var result = await _service.GenerateEventShiftsAsync(new GenerateEventShiftsInput(
            rota.Id, rota.TeamId, 0, 1, [new ShiftTimeSlotInput(new LocalTime(8, 0), 4)], 3, 2));

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Be("MinVolunteers cannot exceed MaxVolunteers.");
    }

    [HumansFact]
    public async Task GenerateEventShifts_Succeeds_WhenMinEqualsMax()
    {
        var (_, rota, _) = SeedRotaScenario(RotaPeriod.Event);
        await SaveAllAsync(Ct);

        var result = await _service.GenerateEventShiftsAsync(new GenerateEventShiftsInput(
            rota.Id, rota.TeamId, 0, 0, [new ShiftTimeSlotInput(new LocalTime(8, 0), 4)], 2, 2));

        result.Succeeded.Should().BeTrue();
        _viewInvalidator.Received(1).InvalidateRota(rota.Id);
    }

    // ============================================================
    // CreateShiftAsync / UpdateShiftAsync — per-period day-offset bounds
    // ============================================================

    [HumansTheory]
    [InlineData(RotaPeriod.Build, -14, -1)]
    [InlineData(RotaPeriod.Event, 0, 6)]
    [InlineData(RotaPeriod.Strike, 7, 9)]
    [InlineData(RotaPeriod.All, -14, 9)]
    public async Task CreateShiftAsync_AcceptsBothEdgesOfTheRotaPeriodWindow(
        RotaPeriod period, int firstDay, int lastDay)
    {
        var (_, rota, _) = SeedRotaScenario(period);
        await SaveAllAsync(Ct);

        foreach (var dayOffset in new[] { firstDay, lastDay })
        {
            var result = await _service.CreateShiftAsync(NewShiftInput(rota, dayOffset));
            result.Succeeded.Should().BeTrue();
        }

        (await ShiftsDb.Shifts.AsNoTracking().Select(s => s.DayOffset).ToListAsync(Ct))
            .Should().BeEquivalentTo(new[] { firstDay, lastDay });
    }

    [HumansTheory]
    [InlineData(RotaPeriod.Build, -15, 0)]
    [InlineData(RotaPeriod.Event, -1, 7)]
    [InlineData(RotaPeriod.Strike, 6, 10)]
    [InlineData(RotaPeriod.All, -15, 10)]
    public async Task CreateShiftAsync_RejectsEitherSideOfTheRotaPeriodWindow(
        RotaPeriod period, int beforeFirst, int afterLast)
    {
        var (_, rota, _) = SeedRotaScenario(period);
        await SaveAllAsync(Ct);

        foreach (var dayOffset in new[] { beforeFirst, afterLast })
        {
            var result = await _service.CreateShiftAsync(NewShiftInput(rota, dayOffset));
            result.Succeeded.Should().BeFalse();
            result.Message.Should().Be("Shift date must fall within the rota's period.");
        }

        ShiftsDb.Shifts.Should().BeEmpty();
    }

    [HumansFact]
    public async Task CreateShiftAsync_Fails_WhenMinExceedsMax()
    {
        var (_, rota, _) = SeedRotaScenario(RotaPeriod.Build);
        await SaveAllAsync(Ct);

        var result = await _service.CreateShiftAsync(NewShiftInput(rota, -3) with
        {
            MinVolunteers = 6,
            MaxVolunteers = 5
        });

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Be("MinVolunteers cannot exceed MaxVolunteers.");
        ShiftsDb.Shifts.Should().BeEmpty();
    }

    [HumansFact]
    public async Task CreateShiftAsync_Succeeds_WhenMinEqualsMax_AndInvalidatesRotaView()
    {
        var (_, rota, _) = SeedRotaScenario(RotaPeriod.Build);
        await SaveAllAsync(Ct);

        var result = await _service.CreateShiftAsync(NewShiftInput(rota, -3) with
        {
            MinVolunteers = 5,
            MaxVolunteers = 5
        });

        result.Succeeded.Should().BeTrue();
        _viewInvalidator.Received(1).InvalidateRota(rota.Id);
    }

    [HumansFact]
    public async Task UpdateShiftAsync_Fails_WhenDayOffsetLeavesTheRotaPeriodWindow()
    {
        var (_, rota, _) = SeedRotaScenario(RotaPeriod.Build);
        var shift = SeedShift(rota, dayOffset: -4);
        await SaveAllAsync(Ct);

        var result = await _service.UpdateShiftAsync(NewUpdateInput(shift, rota.TeamId, dayOffset: 0));

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Be("Shift date must fall within the rota's period.");
        ShiftsDb.ChangeTracker.Clear();
        (await ShiftsDb.Shifts.AsNoTracking().SingleAsync(Ct)).DayOffset.Should().Be(-4);
    }

    [HumansFact]
    public async Task UpdateShiftAsync_Fails_WhenMinExceedsMax()
    {
        var (_, rota, _) = SeedRotaScenario(RotaPeriod.Build);
        var shift = SeedShift(rota, dayOffset: -4);
        await SaveAllAsync(Ct);

        var result = await _service.UpdateShiftAsync(
            NewUpdateInput(shift, rota.TeamId, dayOffset: -3) with { MinVolunteers = 7, MaxVolunteers = 6 });

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Be("MinVolunteers cannot exceed MaxVolunteers.");
        ShiftsDb.ChangeTracker.Clear();
        (await ShiftsDb.Shifts.AsNoTracking().SingleAsync(Ct)).DayOffset.Should().Be(-4);
    }

    [HumansFact]
    public async Task UpdateShiftAsync_InvalidatesTheShiftView()
    {
        var (_, rota, _) = SeedRotaScenario(RotaPeriod.Build);
        var shift = SeedShift(rota, dayOffset: -4);
        await SaveAllAsync(Ct);

        await _service.UpdateShiftAsync(NewUpdateInput(shift, rota.TeamId, dayOffset: -3));

        _viewInvalidator.Received(1).InvalidateShift(shift.Id);
    }

    // ============================================================
    // Helpers
    // ============================================================

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private static TeamInfo ToTeamInfo(Team team) =>
        new(
            team.Id, team.Name, team.Description, team.Slug,
            team.IsActive, team.IsSystemTeam, team.SystemTeamType, team.RequiresApproval,
            team.IsPublicPage, team.IsHidden, team.IsPromotedToDirectory, team.CreatedAt,
            Members: [],
            ParentTeamId: team.ParentTeamId);

    private static CreateShiftInput NewShiftInput(Rota rota, int dayOffset) =>
        new(rota.Id, rota.TeamId, "Gate crew", dayOffset, new LocalTime(8, 0), 4, 2, 5,
            AdminOnly: false, IsAllDay: false);

    private static UpdateShiftInput NewUpdateInput(Shift shift, Guid teamId, int dayOffset) =>
        new(shift.Id, teamId, "Gate crew", dayOffset, new LocalTime(8, 0), 4, 2, 5, AdminOnly: false);

    private static Rota NewRota(Guid eventSettingsId, Guid teamId) =>
        new()
        {
            Id = Guid.NewGuid(),
            EventSettingsId = eventSettingsId,
            TeamId = teamId,
            Name = "New Rota",
            Priority = ShiftPriority.Normal,
            Policy = SignupPolicy.Public,
            Period = RotaPeriod.Event,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        };

    private EventSettings SeedEventSettings(bool isActive = true)
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
            IsActive = isActive,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        };
        ShiftsDb.EventSettings.Add(es);
        return es;
    }

    private Team SeedDepartment(
        string name,
        SystemTeamType systemTeamType = SystemTeamType.None,
        Guid? parentTeamId = null)
    {
        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = name,
            Slug = name.ToLowerInvariant().Replace(" ", "-"),
            SystemTeamType = systemTeamType,
            ParentTeamId = parentTeamId,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        };
        TeamsDb.Teams.Add(team);
        return team;
    }

    private ShiftTag SeedTag(string name)
    {
        var tag = new ShiftTag
        {
            Id = Guid.NewGuid(),
            Name = name
        };
        ShiftsDb.ShiftTags.Add(tag);
        return tag;
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

    private void SeedSignup(Shift shift, Guid userId, SignupStatus status)
    {
        ShiftsDb.ShiftSignups.Add(new ShiftSignup
        {
            Id = Guid.NewGuid(),
            ShiftId = shift.Id,
            UserId = userId,
            Status = status,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        });
    }

    private (EventSettings Es, Rota Rota, Team Team) SeedRotaScenario(RotaPeriod period)
    {
        var es = SeedEventSettings();
        var team = SeedDepartment("Test Department");
        var rota = NewRota(es.Id, team.Id);
        rota.Period = period;
        rota.Name = "Test Rota";
        rota.EventSettings = es;
        ShiftsDb.Rotas.Add(rota);
        return (es, rota, team);
    }
}
