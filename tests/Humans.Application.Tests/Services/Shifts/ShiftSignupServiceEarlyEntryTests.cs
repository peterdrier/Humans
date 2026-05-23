using AwesomeAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using Humans.Application.Services.Shifts;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using ShiftSignupService = Humans.Application.Services.Shifts.ShiftSignupService;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Shifts;
using Humans.Domain.Constants;
using Humans.Infrastructure.Repositories.Shifts;

namespace Humans.Application.Tests.Services.Shifts;

public sealed class ShiftSignupServiceEarlyEntryTests : ServiceTestHarness
{
    private readonly ShiftManagementService _shiftMgmt;
    private readonly ShiftSignupRepository _repo;
    private readonly ShiftSignupService _service;

    private static readonly Instant TestNow = Instant.FromUtc(2026, 6, 15, 12, 0);

    public ShiftSignupServiceEarlyEntryTests()
        : base(TestNow)
    {
        var teamService = Substitute.For<ITeamService>();
        var roleAssignmentService = Substitute.For<IRoleAssignmentService>();
        var serviceProvider = new ServiceLocatorBuilder()
            .With(teamService)
            .With<ITeamServiceRead>(teamService)
            .With(roleAssignmentService)
            .Build();

        var shiftRepo = new ShiftManagementRepository(DbFactory);

        _shiftMgmt = new ShiftManagementService(
            shiftRepo,
            AuditLog,
            AdminAuthorization,
            serviceProvider,
            new MemoryCache(new MemoryCacheOptions()),
            Substitute.For<IShiftViewInvalidator>(),
            Clock,
            NullLogger<ShiftManagementService>.Instance);

        _repo = new ShiftSignupRepository(Db, Clock);
        var membership = Substitute.For<IMembershipCalculator>();
        membership.HasAllRequiredConsentsForTeamAsync(
            Arg.Any<Guid>(), SystemTeamIds.Volunteers, Arg.Any<CancellationToken>())
            .Returns(true);
        _service = new ShiftSignupService(
            _repo,
            _shiftMgmt,
            membership,
            AuditLog,
            Substitute.For<INotificationService>(),
            AdminAuthorization,
            Substitute.For<IShiftViewInvalidator>(),
            serviceProvider,
            Clock,
            NullLogger<ShiftSignupService>.Instance);
    }

    [HumansFact]
    public async Task SignUpAsync_IgnoresCapacityUsageFromDifferentEarlyEntryDay()
    {
        var (eventSettings, rota, _) = SeedBuildScenario();
        eventSettings.EarlyEntryCapacity = new Dictionary<int, int>
        {
            [-3] = 2,
            [-2] = 1
        };

        var existingShift = SeedAllDayShift(rota, -3);
        var targetShift = SeedAllDayShift(rota, -2);
        SeedSignup(Guid.NewGuid(), existingShift.Id, SignupStatus.Confirmed);
        await Db.SaveChangesAsync();

        var result = await _service.SignUpAsync(Guid.NewGuid(), targetShift.Id);

        result.Success.Should().BeTrue();
        result.Warning.Should().BeNull();
    }

    [HumansFact(Timeout = 10000)]
    public async Task SignUpRangeAsync_WarnsWhenLaterEarlyEntryDayIsFull()
    {
        var (eventSettings, rota, _) = SeedBuildScenario();
        eventSettings.EarlyEntryCapacity = new Dictionary<int, int>
        {
            [-3] = 2,
            [-2] = 2,
            [-1] = 1
        };

        SeedAllDayShift(rota, -3);
        SeedAllDayShift(rota, -2);
        var finalDayShift = SeedAllDayShift(rota, -1);
        SeedSignup(Guid.NewGuid(), finalDayShift.Id, SignupStatus.Confirmed);
        await Db.SaveChangesAsync();

        var result = await _service.SignUpRangeAsync(Guid.NewGuid(), rota.Id, -3, -1);

        result.Success.Should().BeTrue();
        result.Warning.Should().Contain("Tue Jun 30");
    }

    private (EventSettings eventSettings, Rota rota, Team team) SeedBuildScenario()
    {
        var eventSettings = new EventSettings
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
        Db.EventSettings.Add(eventSettings);

        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = "Build Department",
            Slug = "build-department",
            SystemTeamType = SystemTeamType.None,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        };
        Db.Teams.Add(team);

        var rota = new Rota
        {
            Id = Guid.NewGuid(),
            EventSettingsId = eventSettings.Id,
            TeamId = team.Id,
            Name = "Build Rota",
            Priority = ShiftPriority.Normal,
            Policy = SignupPolicy.Public,
            Period = RotaPeriod.Build,
            CreatedAt = TestNow,
            UpdatedAt = TestNow,
            EventSettings = eventSettings
        };
        Db.Rotas.Add(rota);

        return (eventSettings, rota, team);
    }

    private Shift SeedAllDayShift(Rota rota, int dayOffset)
    {
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            RotaId = rota.Id,
            DayOffset = dayOffset,
            IsAllDay = true,
            // StartTime/Duration are don't-care for IsAllDay rows; store midnight/24h sentinel.
            StartTime = LocalTime.Midnight,
            Duration = Duration.FromHours(24),
            MinVolunteers = 1,
            MaxVolunteers = 5,
            CreatedAt = TestNow,
            UpdatedAt = TestNow,
            Rota = rota
        };

        Db.Shifts.Add(shift);
        return shift;
    }

    private void SeedSignup(Guid userId, Guid shiftId, SignupStatus status)
    {
        Db.ShiftSignups.Add(new ShiftSignup
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ShiftId = shiftId,
            Status = status,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        });
    }
}
