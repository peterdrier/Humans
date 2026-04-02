using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Services;
using Xunit;

namespace Humans.Application.Tests.Services;

public class ShiftSignupServiceEarlyEntryTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly IAuditLogService _auditLog;
    private readonly ShiftManagementService _shiftMgmt;
    private readonly ShiftSignupService _service;

    private static readonly Instant TestNow = Instant.FromUtc(2026, 6, 15, 12, 0);

    public ShiftSignupServiceEarlyEntryTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new HumansDbContext(options);
        _clock = new FakeClock(TestNow);
        _auditLog = Substitute.For<IAuditLogService>();

        _shiftMgmt = new ShiftManagementService(
            _dbContext,
            _auditLog,
            new MemoryCache(new MemoryCacheOptions()),
            _clock,
            NullLogger<ShiftManagementService>.Instance);

        _service = new ShiftSignupService(
            _dbContext,
            _shiftMgmt,
            _auditLog,
            Substitute.For<INotificationService>(),
            _clock,
            NullLogger<ShiftSignupService>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
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
        await _dbContext.SaveChangesAsync();

        var result = await _service.SignUpAsync(Guid.NewGuid(), targetShift.Id);

        result.Success.Should().BeTrue();
        result.Warning.Should().BeNull();
    }

    [Fact]
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
        await _dbContext.SaveChangesAsync();

        var result = await _service.SignUpRangeAsync(Guid.NewGuid(), rota.Id, -3, -1);

        result.Success.Should().BeTrue();
        result.Warning.Should().Contain("-1");
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
        _dbContext.EventSettings.Add(eventSettings);

        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = "Build Department",
            Slug = "build-department",
            SystemTeamType = SystemTeamType.None,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        };
        _dbContext.Teams.Add(team);

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
            EventSettings = eventSettings,
            Team = team
        };
        _dbContext.Rotas.Add(rota);

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
            StartTime = new LocalTime(0, 0),
            Duration = Duration.FromHours(24),
            MinVolunteers = 1,
            MaxVolunteers = 5,
            CreatedAt = TestNow,
            UpdatedAt = TestNow,
            Rota = rota
        };

        _dbContext.Shifts.Add(shift);
        return shift;
    }

    private void SeedSignup(Guid userId, Guid shiftId, SignupStatus status)
    {
        _dbContext.ShiftSignups.Add(new ShiftSignup
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
