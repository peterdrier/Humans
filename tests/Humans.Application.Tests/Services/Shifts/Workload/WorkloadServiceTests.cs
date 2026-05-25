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
    private readonly ITeamService _teamService = Substitute.For<ITeamService>();

    public WorkloadServiceTests() : base(Instant.FromUtc(2026, 7, 1, 12, 0))
    {
        var repo = new ShiftManagementRepository(DbFactory);

        // IShiftView source-of-truth path uses GetRotaAsync only — the inner
        // ShiftViewService also takes signup/availability/tracking repos for
        // GetUserAsync, which the workload service does not call. Stub them.
        var view = new ShiftViewService(
            repo,
            Substitute.For<IShiftSignupRepository>(),
            Substitute.For<IGeneralAvailabilityRepository>(),
            Substitute.For<IVolunteerTrackingRepository>());

        _teamService.GetByIdsWithParentsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => GetTeamsByIdsAsync(call.Arg<IReadOnlyCollection<Guid>>()));

        _service = new WorkloadService(repo, view, _teamService, NewDbBackedUserService());
    }

    private async Task<IReadOnlyDictionary<Guid, Team>> GetTeamsByIdsAsync(IReadOnlyCollection<Guid> ids)
    {
        if (ids.Count == 0) return new Dictionary<Guid, Team>();
        var teams = await Db.Teams.Where(t => ids.Contains(t.Id)).ToListAsync();
        return teams.ToDictionary(t => t.Id);
    }

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
