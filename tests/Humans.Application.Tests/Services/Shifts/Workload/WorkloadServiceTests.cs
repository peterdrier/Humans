using AwesomeAssertions;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Services.Shifts;
using Humans.Application.Services.Shifts.Workload;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Repositories.Shifts;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NSubstitute;

namespace Humans.Application.Tests.Services.Shifts.Workload;

/// <summary>
/// Behaviour tests for <see cref="WorkloadService"/> — verifies that
/// per-person hour totals (split by Build/Event/Strike), per-rota roll-ups,
/// and per-department roll-ups match what the spec at
/// nobodies-collective/Humans#734 promises.
/// </summary>
public sealed class WorkloadServiceTests : ServiceTestHarness
{
    private readonly WorkloadService _service;
    private readonly ITeamServiceRead _teamService = Substitute.For<ITeamServiceRead>();

    public WorkloadServiceTests() : base(Instant.FromUtc(2026, 7, 1, 12, 0))
    {
        var repo = new ShiftRepository(DbFactory, Db, Clock);

        // IShiftView source-of-truth path uses GetRotaAsync only — the inner
        // ShiftViewService also takes signup/availability/tracking repos for
        // GetUserAsync, which the workload service does not call. Stub them.
        var view = new ShiftViewService(
            repo,
            Substitute.For<IVolunteerTrackingRepository>());

        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(_ => GetTeamInfosAsync());

        _service = new WorkloadService(repo, view, _teamService, NewDbBackedUserService());
    }

    private Task<IReadOnlyDictionary<Guid, TeamInfo>> GetTeamInfosAsync() =>
        Task.FromResult<IReadOnlyDictionary<Guid, TeamInfo>>(
            Db.Teams.AsEnumerable().ToDictionary(
                t => t.Id,
                t => new TeamInfo(
                    t.Id, t.Name, t.Description, t.Slug,
                    t.IsActive, t.IsSystemTeam, t.SystemTeamType, t.RequiresApproval,
                    t.IsPublicPage, t.IsHidden, t.IsPromotedToDirectory, t.CreatedAt,
                    Members: [],
                    ParentTeamId: t.ParentTeamId)));

    [HumansFact]
    public async Task GetForActiveEvent_NoActiveEvent_ReturnsNull()
    {
        var result = await _service.GetForActiveEventAsync();
        result.Should().BeNull();
    }

    [HumansFact]
    public async Task GetForActiveEvent_EmptyEvent_ReturnsEmptyReport()
    {
        var es = await SeedEventAsync();

        var report = await _service.GetForActiveEventAsync();

        report.Should().NotBeNull();
        report.EventSettingsId.Should().Be(es.Id);
        report.EventYear.Should().Be(2026);
        report.ByPerson.Should().BeEmpty();
        report.ByRota.Should().BeEmpty();
        report.ByDepartment.Should().BeEmpty();
    }

    [HumansFact]
    public async Task ByPerson_SumsConfirmedHours_PendingDoesNotInflateHours()
    {
        var es = await SeedEventAsync();
        var team = await SeedWorkloadTeamAsync("Gate");
        var rota = await SeedRotaAsync(team, es);
        var s1 = await SeedShiftAsync(rota, dayOffset: 1, hours: 4);
        var s2 = await SeedShiftAsync(rota, dayOffset: 2, hours: 6);

        var alice = await SeedUserWithProfileAsync("Alice");
        var bob = await SeedUserWithProfileAsync("Bob");
        await SeedSignupAsync(s1, alice.Id, SignupStatus.Confirmed);
        await SeedSignupAsync(s2, alice.Id, SignupStatus.Confirmed);
        await SeedSignupAsync(s1, bob.Id, SignupStatus.Pending);

        var report = await _service.GetForActiveEventAsync();
        report.Should().NotBeNull();

        var aliceRow = report.ByPerson.Single(p => p.UserId == alice.Id);
        aliceRow.TotalHours.Should().Be(10m);
        aliceRow.EventHours.Should().Be(10m); // both day offsets 1 and 2 → Event phase
        aliceRow.BuildHours.Should().Be(0m);
        aliceRow.StrikeHours.Should().Be(0m);
        aliceRow.ConfirmedSignupCount.Should().Be(2);
        aliceRow.PendingSignupCount.Should().Be(0);

        var bobRow = report.ByPerson.Single(p => p.UserId == bob.Id);
        bobRow.TotalHours.Should().Be(0m);
        bobRow.ConfirmedSignupCount.Should().Be(0);
        bobRow.PendingSignupCount.Should().Be(1);
    }

    [HumansFact]
    public async Task ByPerson_SplitsHoursByPeriod_BuildEventStrike()
    {
        // EventEndOffset=6 / StrikeEndOffset=9 from SeedEventAsync.
        // DayOffset<0 → Build, 0..6 → Event, 7+ → Strike.
        var es = await SeedEventAsync();
        var team = await SeedWorkloadTeamAsync("Gate");
        var rota = await SeedRotaAsync(team, es);
        var buildShift = await SeedShiftAsync(rota, dayOffset: -2, hours: 4);
        var eventShift = await SeedShiftAsync(rota, dayOffset: 1, hours: 5);
        var strikeShift = await SeedShiftAsync(rota, dayOffset: 8, hours: 3);

        var alice = await SeedUserWithProfileAsync("Alice");
        await SeedSignupAsync(buildShift, alice.Id, SignupStatus.Confirmed);
        await SeedSignupAsync(eventShift, alice.Id, SignupStatus.Confirmed);
        await SeedSignupAsync(strikeShift, alice.Id, SignupStatus.Confirmed);

        var report = await _service.GetForActiveEventAsync();
        report.Should().NotBeNull();
        var row = report.ByPerson.Single(p => p.UserId == alice.Id);
        row.BuildHours.Should().Be(4m);
        row.EventHours.Should().Be(5m);
        row.StrikeHours.Should().Be(3m);
        row.TotalHours.Should().Be(12m);
        row.ConfirmedSignupCount.Should().Be(3);
    }

    [HumansFact]
    public async Task ByDepartment_CountsFilledSlotsAndHoursCappedAtMax_AndIncludesSlug()
    {
        var es = await SeedEventAsync();
        var team = await SeedWorkloadTeamAsync("Gate");
        var rota = await SeedRotaAsync(team, es);
        var shift = await SeedShiftAsync(rota, dayOffset: 1, hours: 4, max: 3);

        for (var i = 0; i < 5; i++)
        {
            var u = await SeedUserWithProfileAsync($"u{i}");
            await SeedSignupAsync(shift, u.Id, SignupStatus.Confirmed);
        }

        var report = await _service.GetForActiveEventAsync();
        report.Should().NotBeNull();
        var dept = report.ByDepartment.Single();
        dept.PlannedSlots.Should().Be(3);
        dept.FilledSlots.Should().Be(3); // capped at MaxVolunteers
        dept.PlannedHours.Should().Be(12m); // 4h * 3 slots
        dept.FilledHours.Should().Be(12m); // capped
        dept.TeamSlug.Should().Be("gate");
    }

    [HumansFact]
    public async Task ByPerson_RoleHours_MappedByPeriod_YearRoundIntoOwnColumn_OthersFoldIntoShiftColumns()
    {
        // Event has shifts (so the report isn't short-circuited). Alice holds two
        // roles: a year-round role (10h) and a Build-period role (4h), plus a
        // 5h Event shift signup. Year-round lands in its own column; Build role
        // hours fold into the Build shift column.
        var es = await SeedEventAsync();
        var team = await SeedWorkloadTeamAsync("Gate");
        var rota = await SeedRotaAsync(team, es);
        var eventShift = await SeedShiftAsync(rota, dayOffset: 1, hours: 5);
        var alice = await SeedUserWithProfileAsync("Alice");
        await SeedSignupAsync(eventShift, alice.Id, SignupStatus.Confirmed);

        StubTeams(TeamWithRoles(team.Id, "Gate", "gate",
            Role(team.Id, "Gate", "gate", RolePeriod.YearRound, estimatedHours: 10, slotCount: 1, alice.Id),
            Role(team.Id, "Gate", "gate", RolePeriod.Build, estimatedHours: 4, slotCount: 1, alice.Id)));

        var report = await _service.GetForActiveEventAsync();
        report.Should().NotBeNull();
        var row = report.ByPerson.Single(p => p.UserId == alice.Id);
        row.YearRoundHours.Should().Be(10m);
        row.BuildHours.Should().Be(4m);
        row.EventHours.Should().Be(5m);
        row.StrikeHours.Should().Be(0m);
        row.TotalHours.Should().Be(19m);
    }

    [HumansFact]
    public async Task ByPerson_RoleOnlyHolder_AppearsWithNoSignups()
    {
        // The event has a shift (someone else, or none) so the report renders.
        // Bob holds only a year-round role and has zero shift signups — he must
        // still appear, with his role hours.
        var es = await SeedEventAsync();
        var team = await SeedWorkloadTeamAsync("Board");
        var rota = await SeedRotaAsync(team, es);
        await SeedShiftAsync(rota, dayOffset: 1, hours: 5); // un-signed shift, just to render the report

        var bob = await SeedUserWithProfileAsync("Bob");
        StubTeams(TeamWithRoles(team.Id, "Board", "board",
            Role(team.Id, "Board", "board", RolePeriod.YearRound, estimatedHours: 12, slotCount: 1, bob.Id)));

        var report = await _service.GetForActiveEventAsync();
        report.Should().NotBeNull();
        var row = report.ByPerson.Single(p => p.UserId == bob.Id);
        row.YearRoundHours.Should().Be(12m);
        row.TotalHours.Should().Be(12m);
        row.ConfirmedSignupCount.Should().Be(0);
        row.PendingSignupCount.Should().Be(0);
    }

    [HumansFact]
    public async Task ByPerson_RoleWithoutEstimatedHours_ContributesNothing()
    {
        var es = await SeedEventAsync();
        var team = await SeedWorkloadTeamAsync("Gate");
        var rota = await SeedRotaAsync(team, es);
        var shift = await SeedShiftAsync(rota, dayOffset: 1, hours: 5);
        var alice = await SeedUserWithProfileAsync("Alice");
        await SeedSignupAsync(shift, alice.Id, SignupStatus.Confirmed);

        StubTeams(TeamWithRoles(team.Id, "Gate", "gate",
            Role(team.Id, "Gate", "gate", RolePeriod.YearRound, estimatedHours: null, slotCount: 1, alice.Id)));

        var report = await _service.GetForActiveEventAsync();
        report.Should().NotBeNull();
        var row = report.ByPerson.Single(p => p.UserId == alice.Id);
        row.YearRoundHours.Should().Be(0m);
        row.TotalHours.Should().Be(5m); // shift only
    }

    [HumansFact]
    public async Task ByDepartment_FoldsRolePlannedAndFilledHours()
    {
        // Gate has a 4h shift with 3 slots, 0 confirmed → shift planned 12, filled 0.
        // Gate also owns a 10h role with 2 slots, 1 filled → role planned 20, filled 10.
        var es = await SeedEventAsync();
        var team = await SeedWorkloadTeamAsync("Gate");
        var rota = await SeedRotaAsync(team, es);
        await SeedShiftAsync(rota, dayOffset: 1, hours: 4, max: 3);
        var alice = await SeedUserWithProfileAsync("Alice");

        StubTeams(TeamWithRoles(team.Id, "Gate", "gate",
            Role(team.Id, "Gate", "gate", RolePeriod.Event, estimatedHours: 10, slotCount: 2, alice.Id)));

        var report = await _service.GetForActiveEventAsync();
        report.Should().NotBeNull();
        var dept = report.ByDepartment.Single(d => d.TeamId == team.Id);
        dept.PlannedHours.Should().Be(32m); // 12 shift + 20 role
        dept.FilledHours.Should().Be(10m); // 0 shift + 10 role
        dept.PlannedSlots.Should().Be(3); // slots remain shift-only
    }

    [HumansFact]
    public async Task ByDepartment_RoleOnlyTeam_AppearsWithZeroShiftColumns()
    {
        // Gate has shifts (renders the report). Board owns a role but no rotas —
        // it must still appear as a department row, with shift columns at zero.
        var es = await SeedEventAsync();
        var gate = await SeedWorkloadTeamAsync("Gate");
        var gateRota = await SeedRotaAsync(gate, es);
        await SeedShiftAsync(gateRota, dayOffset: 1, hours: 4);

        var board = await SeedWorkloadTeamAsync("Board");
        var bob = await SeedUserWithProfileAsync("Bob");

        StubTeams(
            TeamWithRoles(board.Id, "Board", "board",
                Role(board.Id, "Board", "board", RolePeriod.YearRound, estimatedHours: 8, slotCount: 1, bob.Id)));

        var report = await _service.GetForActiveEventAsync();
        report.Should().NotBeNull();
        var dept = report.ByDepartment.Single(d => d.TeamId == board.Id);
        dept.TeamName.Should().Be("Board");
        dept.TeamSlug.Should().Be("board");
        dept.RotaCount.Should().Be(0);
        dept.ShiftCount.Should().Be(0);
        dept.PlannedSlots.Should().Be(0);
        dept.PlannedHours.Should().Be(8m);
        dept.FilledHours.Should().Be(8m);
    }

    [HumansFact]
    public async Task RoleHours_FromInactiveTeams_AreExcluded()
    {
        // A retired (deactivated) team can still carry role assignments — they
        // aren't cleared on deactivation — but its role hours must not leak into
        // the workload report.
        var es = await SeedEventAsync();
        var gate = await SeedWorkloadTeamAsync("Gate");
        var rota = await SeedRotaAsync(gate, es);
        await SeedShiftAsync(rota, dayOffset: 1, hours: 5); // renders the report

        var oldTeam = await SeedWorkloadTeamAsync("OldTeam");
        var alice = await SeedUserWithProfileAsync("Alice");

        StubTeams(
            TeamWithRoles(oldTeam.Id, "OldTeam", "oldteam",
                Role(oldTeam.Id, "OldTeam", "oldteam", RolePeriod.YearRound, estimatedHours: 40, slotCount: 1, alice.Id))
            with
            { IsActive = false });

        var report = await _service.GetForActiveEventAsync();
        report.Should().NotBeNull();
        report.ByPerson.Should().NotContain(p => p.UserId == alice.Id);
        report.ByDepartment.Should().NotContain(d => d.TeamId == oldTeam.Id);
    }

    [HumansFact]
    public async Task ByRota_IncludesAdminOnlyAndHiddenRotas()
    {
        // Workload view is admin-only — coordinators need full visibility for
        // balancing, including admin-only shifts and hidden rotas.
        var es = await SeedEventAsync();
        var team = await SeedWorkloadTeamAsync("Gate");
        var hiddenRota = await SeedRotaAsync(team, es, isVisible: false);
        var visibleRota = await SeedRotaAsync(team, es);
        await SeedShiftAsync(visibleRota, dayOffset: 1, hours: 4, adminOnly: true);
        await SeedShiftAsync(hiddenRota, dayOffset: 2, hours: 4);

        var report = await _service.GetForActiveEventAsync();
        report.Should().NotBeNull();
        report.ByRota.Should().HaveCount(2);
        report.ByRota.Select(r => r.RotaId).Should().BeEquivalentTo([visibleRota.Id, hiddenRota.Id]);
    }

    [HumansFact]
    public async Task ByRota_RollsUpShiftsPerRota()
    {
        var es = await SeedEventAsync();
        var team = await SeedWorkloadTeamAsync("Gate");
        var rota = await SeedRotaAsync(team, es);
        await SeedShiftAsync(rota, dayOffset: 1, hours: 4, max: 2);
        await SeedShiftAsync(rota, dayOffset: 2, hours: 6, max: 3);

        var report = await _service.GetForActiveEventAsync();
        report.Should().NotBeNull();
        var row = report.ByRota.Single(r => r.RotaId == rota.Id);
        row.ShiftCount.Should().Be(2);
        row.PlannedSlots.Should().Be(5);
        row.PlannedHours.Should().Be(4m * 2 + 6m * 3);
        row.FilledSlots.Should().Be(0);
        row.FilledHours.Should().Be(0m);
        row.TeamName.Should().Be("Gate");
    }

    [HumansFact]
    public async Task AllDayShift_UsesEightToSixWindow()
    {
        // All-day shifts contribute the standard 08:00–18:00 window
        // regardless of nominal Duration.
        var es = await SeedEventAsync();
        var team = await SeedWorkloadTeamAsync("Build");
        var rota = await SeedRotaAsync(team, es);
        var allDay = await SeedAllDayShiftAsync(rota, dayOffset: -3, nominalHours: 24);

        var alice = await SeedUserWithProfileAsync("Alice");
        await SeedSignupAsync(allDay, alice.Id, SignupStatus.Confirmed);

        var report = await _service.GetForActiveEventAsync();
        report.Should().NotBeNull();
        report.ByPerson.Single(p => p.UserId == alice.Id).BuildHours.Should().Be(10m); // 18:00 - 08:00
    }

    // ── Role-hours fixtures (cached TeamInfo projection the service reads) ─────────

    private void StubTeams(params TeamInfo[] teams) =>
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(teams.ToDictionary(t => t.Id));

    private static TeamInfo TeamWithRoles(
        Guid teamId, string name, string slug, params TeamRoleDefinitionSnapshot[] roles) =>
        new(teamId, name, null, slug,
            IsActive: true, IsSystemTeam: false, SystemTeamType.None, RequiresApproval: false,
            IsPublicPage: true, IsHidden: false, IsPromotedToDirectory: false,
            CreatedAt: Instant.FromUtc(2026, 1, 1, 0, 0), Members: [],
            RoleDefinitions: roles);

    private static TeamRoleDefinitionSnapshot Role(
        Guid teamId, string teamName, string teamSlug,
        RolePeriod period, int? estimatedHours, int slotCount, params Guid?[] assignedUserIds)
    {
        var assignments = assignedUserIds
            .Select((uid, i) => new TeamRoleAssignmentSnapshot(Guid.NewGuid(), Guid.NewGuid(), i, uid))
            .ToList();
        return new TeamRoleDefinitionSnapshot(
            Guid.NewGuid(), teamId, teamName, teamSlug, "Role", null, slotCount, estimatedHours,
            [], 0, false, period, true, assignments);
    }

    // ── Test-local seeders (Workload-specific shape; harness covers User/Team/etc.) ─

    private async Task<EventSettings> SeedEventAsync()
    {
        var now = Clock.GetCurrentInstant();
        var es = new EventSettings
        {
            Id = Guid.NewGuid(),
            EventName = "Test Event",
            Year = 2026,
            TimeZoneId = "UTC",
            GateOpeningDate = new LocalDate(2026, 8, 1),
            BuildStartOffset = -14,
            EventEndOffset = 6,
            StrikeEndOffset = 9,
            IsActive = true,
            CreatedAt = now.Minus(Duration.FromDays(60)),
            UpdatedAt = now,
        };
        Db.EventSettings.Add(es);
        await Db.SaveChangesAsync();
        return es;
    }

    // Harness SeedTeam returns synchronously and doesn't save; workload tests use
    // an async save-then-return shape so the entity is queryable by the service.
    private async Task<Team> SeedWorkloadTeamAsync(string name)
    {
        var team = SeedTeam(name);
        await Db.SaveChangesAsync();
        return team;
    }

    private async Task<Rota> SeedRotaAsync(Team team, EventSettings es, bool isVisible = true)
    {
        var now = Clock.GetCurrentInstant();
        var rota = new Rota
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            EventSettingsId = es.Id,
            Name = $"{team.Name} rota",
            Priority = ShiftPriority.Normal,
            Policy = SignupPolicy.Public,
            Period = RotaPeriod.Event,
            IsVisibleToVolunteers = isVisible,
            CreatedAt = now,
            UpdatedAt = now,
        };
        Db.Rotas.Add(rota);
        await Db.SaveChangesAsync();
        return rota;
    }

    private async Task<Shift> SeedShiftAsync(Rota rota, int dayOffset, int hours, int max = 5, bool adminOnly = false)
    {
        var now = Clock.GetCurrentInstant();
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            RotaId = rota.Id,
            DayOffset = dayOffset,
            StartTime = new LocalTime(9, 0),
            Duration = Duration.FromHours(hours),
            IsAllDay = false,
            MinVolunteers = 1,
            MaxVolunteers = max,
            AdminOnly = adminOnly,
            CreatedAt = now,
            UpdatedAt = now,
        };
        Db.Shifts.Add(shift);
        await Db.SaveChangesAsync();
        return shift;
    }

    private async Task<Shift> SeedAllDayShiftAsync(Rota rota, int dayOffset, double nominalHours)
    {
        var now = Clock.GetCurrentInstant();
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            RotaId = rota.Id,
            DayOffset = dayOffset,
            StartTime = new LocalTime(8, 0),
            Duration = Duration.FromHours(nominalHours),
            IsAllDay = true,
            MinVolunteers = 1,
            MaxVolunteers = 3,
            CreatedAt = now,
            UpdatedAt = now,
        };
        Db.Shifts.Add(shift);
        await Db.SaveChangesAsync();
        return shift;
    }

    // Workload reads display name from Profile.BurnerName (not User.DisplayName).
    // Harness SeedUser doesn't create a Profile, so this test class layers one on.
    private async Task<User> SeedUserWithProfileAsync(string burnerName)
    {
        var now = Clock.GetCurrentInstant();
        var user = SeedUser(displayName: burnerName);
        Db.Profiles.Add(new Profile
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            BurnerName = burnerName,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await Db.SaveChangesAsync();
        return user;
    }

    private async Task SeedSignupAsync(Shift shift, Guid userId, SignupStatus status)
    {
        var now = Clock.GetCurrentInstant();
        Db.ShiftSignups.Add(new ShiftSignup
        {
            Id = Guid.NewGuid(),
            ShiftId = shift.Id,
            UserId = userId,
            Status = status,
            CreatedAt = now.Minus(Duration.FromHours(1)),
            UpdatedAt = now,
        });
        await Db.SaveChangesAsync();
    }
}
