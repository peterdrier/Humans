using AwesomeAssertions;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Shifts;
using Humans.Application.Services.Shifts.Workload;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
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
public class WorkloadServiceTests : IDisposable
{
    private static readonly Instant TestNow = Instant.FromUtc(2026, 7, 1, 12, 0);

    private readonly HumansDbContext _dbContext;
    private readonly WorkloadService _service;
    private readonly ITeamService _teamService = Substitute.For<ITeamService>();
    private readonly IUserService _userService = Substitute.For<IUserService>();

    public WorkloadServiceTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new HumansDbContext(options);

        var repo = new ShiftManagementRepository(new TestDbContextFactory(options));

        // IShiftView source-of-truth path uses GetRotaAsync only — the inner
        // ShiftViewService also takes signup/availability/tracking repos for
        // GetUserAsync, which the workload service does not call. Stub them.
        var view = new ShiftViewService(
            repo,
            Substitute.For<IShiftSignupRepository>(),
            Substitute.For<IGeneralAvailabilityRepository>(),
            Substitute.For<IVolunteerTrackingRepository>());

        // Wire team/user lookups against the same in-memory DB so test seeds
        // drive both the EF reads and the cross-section name stitching.
        _teamService.GetByIdsWithParentsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => GetTeamsByIdsAsync(call.Arg<IReadOnlyCollection<Guid>>()));
        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => GetUserInfosAsync(call.Arg<IReadOnlyCollection<Guid>>()));

        _service = new WorkloadService(repo, view, _teamService, _userService);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<IReadOnlyDictionary<Guid, Team>> GetTeamsByIdsAsync(IReadOnlyCollection<Guid> ids)
    {
        if (ids.Count == 0) return new Dictionary<Guid, Team>();
        var teams = await _dbContext.Teams.Where(t => ids.Contains(t.Id)).ToListAsync();
        return teams.ToDictionary(t => t.Id);
    }

    private async ValueTask<IReadOnlyDictionary<Guid, UserInfo>> GetUserInfosAsync(IReadOnlyCollection<Guid> ids)
    {
        if (ids.Count == 0) return new Dictionary<Guid, UserInfo>();
        var users = await _dbContext.Users.Where(u => ids.Contains(u.Id)).ToListAsync();
        var profiles = await _dbContext.Profiles
            .Where(p => ids.Contains(p.UserId))
            .ToDictionaryAsync(p => p.UserId);
        return users.ToDictionary(
            u => u.Id,
            u => UserInfo.Create(
                u,
                userEmails: [],
                eventParticipations: [],
                externalLogins: [],
                profile: profiles.GetValueOrDefault(u.Id),
                contactFields: [],
                profileLanguages: [],
                volunteerHistory: [],
                communicationPreferences: []));
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
        var team = await SeedTeamAsync("Gate");
        var rota = await SeedRotaAsync(team, es);
        var s1 = await SeedShiftAsync(rota, dayOffset: 1, hours: 4);
        var s2 = await SeedShiftAsync(rota, dayOffset: 2, hours: 6);

        var alice = await SeedUserAsync("Alice");
        var bob = await SeedUserAsync("Bob");
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
        var team = await SeedTeamAsync("Gate");
        var rota = await SeedRotaAsync(team, es);
        var buildShift = await SeedShiftAsync(rota, dayOffset: -2, hours: 4);
        var eventShift = await SeedShiftAsync(rota, dayOffset: 1, hours: 5);
        var strikeShift = await SeedShiftAsync(rota, dayOffset: 8, hours: 3);

        var alice = await SeedUserAsync("Alice");
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
        var team = await SeedTeamAsync("Gate");
        var rota = await SeedRotaAsync(team, es);
        var shift = await SeedShiftAsync(rota, dayOffset: 1, hours: 4, max: 3);

        for (var i = 0; i < 5; i++)
        {
            var u = await SeedUserAsync($"u{i}");
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
        var team = await SeedTeamAsync("Gate");
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
        var team = await SeedTeamAsync("Gate");
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
        var team = await SeedTeamAsync("Build");
        var rota = await SeedRotaAsync(team, es);
        var allDay = await SeedAllDayShiftAsync(rota, dayOffset: -3, nominalHours: 24);

        var alice = await SeedUserAsync("Alice");
        await SeedSignupAsync(allDay, alice.Id, SignupStatus.Confirmed);

        var report = await _service.GetForActiveEventAsync();
        report.Should().NotBeNull();
        report.ByPerson.Single(p => p.UserId == alice.Id).BuildHours.Should().Be(10m); // 18:00 - 08:00
    }

    // ── Seed helpers ────────────────────────────────────────────────────────

    private async Task<EventSettings> SeedEventAsync()
    {
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
            CreatedAt = TestNow.Minus(Duration.FromDays(60)),
            UpdatedAt = TestNow,
        };
        _dbContext.EventSettings.Add(es);
        await _dbContext.SaveChangesAsync();
        return es;
    }

    private async Task<Team> SeedTeamAsync(string name)
    {
        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = name,
            Slug = name.ToLowerInvariant(),
            IsActive = true,
            CreatedAt = TestNow,
            UpdatedAt = TestNow,
        };
        _dbContext.Teams.Add(team);
        await _dbContext.SaveChangesAsync();
        return team;
    }

    private async Task<Rota> SeedRotaAsync(Team team, EventSettings es, bool isVisible = true)
    {
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
            CreatedAt = TestNow,
            UpdatedAt = TestNow,
        };
        _dbContext.Rotas.Add(rota);
        await _dbContext.SaveChangesAsync();
        return rota;
    }

    private async Task<Shift> SeedShiftAsync(Rota rota, int dayOffset, int hours, int max = 5, bool adminOnly = false)
    {
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
            CreatedAt = TestNow,
            UpdatedAt = TestNow,
        };
        _dbContext.Shifts.Add(shift);
        await _dbContext.SaveChangesAsync();
        return shift;
    }

    private async Task<Shift> SeedAllDayShiftAsync(Rota rota, int dayOffset, double nominalHours)
    {
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
            CreatedAt = TestNow,
            UpdatedAt = TestNow,
        };
        _dbContext.Shifts.Add(shift);
        await _dbContext.SaveChangesAsync();
        return shift;
    }

    private async Task<User> SeedUserAsync(string displayName)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            UserName = $"{displayName}@test",
            NormalizedUserName = $"{displayName.ToUpperInvariant()}@TEST",
            Email = $"{displayName}@test",
            NormalizedEmail = $"{displayName.ToUpperInvariant()}@TEST",
            SecurityStamp = Guid.NewGuid().ToString(),
            CreatedAt = TestNow,
        };
        _dbContext.Users.Add(user);
        _dbContext.Profiles.Add(new Profile
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            BurnerName = displayName,
            CreatedAt = TestNow,
            UpdatedAt = TestNow,
        });
        await _dbContext.SaveChangesAsync();
        return user;
    }

    private async Task SeedSignupAsync(Shift shift, Guid userId, SignupStatus status)
    {
        _dbContext.ShiftSignups.Add(new ShiftSignup
        {
            Id = Guid.NewGuid(),
            ShiftId = shift.Id,
            UserId = userId,
            Status = status,
            CreatedAt = TestNow.Minus(Duration.FromHours(1)),
            UpdatedAt = TestNow,
        });
        await _dbContext.SaveChangesAsync();
    }
}
