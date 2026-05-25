using AwesomeAssertions;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.EarlyEntry;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Services.Shifts;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories.Shifts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Services.Shifts;

/// <summary>
/// Tests for <see cref="ShiftSignupService.PeekRangeShiftsAsync"/> — the
/// read-only helper that surfaces the candidate all-day shifts a future
/// <see cref="ShiftSignupService.SignUpRangeAsync"/> call would target.
/// Consumed by the dietary-prompt gate (issue #279) so it can peek the
/// range before going through the signup write path.
/// </summary>
public class ShiftSignupServicePeekRangeShiftsTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly ShiftSignupService _service;

    private static readonly Instant TestNow = Instant.FromUtc(2026, 6, 15, 12, 0);

    private readonly EventSettings _es;
    private readonly Team _team;

    public ShiftSignupServicePeekRangeShiftsTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new HumansDbContext(options);
        var clock = new FakeClock(TestNow);
        var auditLog = Substitute.For<IAuditLogService>();
        var membership = Substitute.For<IMembershipCalculator>();

        var teamService = Substitute.For<ITeamService>();
        var roleAssignmentService = Substitute.For<IRoleAssignmentService>();
        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(ITeamService)).Returns(teamService);
        serviceProvider.GetService(typeof(IRoleAssignmentService)).Returns(roleAssignmentService);

        var adminAuthorization = Substitute.For<IAdminAuthorizationService>();

        var shiftRepo = new ShiftManagementRepository(new TestDbContextFactory(options));
        var shiftMgmt = new ShiftManagementService(
            shiftRepo,
            auditLog,
            adminAuthorization,
            serviceProvider,
            new MemoryCache(new MemoryCacheOptions()),
            Substitute.For<IShiftViewInvalidator>(),
            clock,
            NullLogger<ShiftManagementService>.Instance);

        var repo = new ShiftSignupRepository(_dbContext, clock);
        _service = new ShiftSignupService(
            repo,
            shiftMgmt,
            membership,
            auditLog,
            Substitute.For<INotificationService>(),
            adminAuthorization,
            Substitute.For<IShiftViewInvalidator>(),
            Substitute.For<IEarlyEntryInvalidator>(),
            serviceProvider,
            clock,
            NullLogger<ShiftSignupService>.Instance);

        _es = new EventSettings
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
        _dbContext.EventSettings.Add(_es);

        _team = new Team
        {
            Id = Guid.NewGuid(),
            Name = "Test Department",
            Slug = "test-dept",
            SystemTeamType = SystemTeamType.None,
            ParentTeamId = null,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        };
        _dbContext.Teams.Add(_team);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [HumansFact]
    public async Task ReturnsAllDayShiftsInOffsetWindow()
    {
        var rota = SeedRota();
        SeedShift(rota, dayOffset: 0, isAllDay: true);
        SeedShift(rota, dayOffset: 1, isAllDay: true);
        SeedShift(rota, dayOffset: 1, isAllDay: false);
        SeedShift(rota, dayOffset: 2, isAllDay: true);
        SeedShift(rota, dayOffset: 5, isAllDay: true);
        await _dbContext.SaveChangesAsync();

        var result = await _service.PeekRangeShiftsAsync(rota.Id, startDayOffset: 0, endDayOffset: 2);

        result.Should().HaveCount(3);
        result.Select(s => s.DayOffset).Should().BeEquivalentTo(new[] { 0, 1, 2 });
        result.Should().OnlyContain(s => s.IsAllDay);
    }

    [HumansFact]
    public async Task ReturnsEmptyWhenNoShiftsInWindow()
    {
        var rota = SeedRota();
        SeedShift(rota, dayOffset: 10, isAllDay: true);
        await _dbContext.SaveChangesAsync();

        var result = await _service.PeekRangeShiftsAsync(rota.Id, startDayOffset: 0, endDayOffset: 2);

        result.Should().BeEmpty();
    }

    [HumansFact]
    public async Task ReturnsEmptyWhenRotaNotFound()
    {
        var result = await _service.PeekRangeShiftsAsync(
            rotaId: Guid.NewGuid(), startDayOffset: 0, endDayOffset: 2);

        result.Should().BeEmpty();
    }

    private Rota SeedRota()
    {
        var rota = new Rota
        {
            Id = Guid.NewGuid(),
            EventSettingsId = _es.Id,
            TeamId = _team.Id,
            Name = "Test Rota",
            Priority = ShiftPriority.Normal,
            Policy = SignupPolicy.Public,
            Period = RotaPeriod.Build,
            CreatedAt = TestNow,
            UpdatedAt = TestNow,
            EventSettings = _es
        };
        _dbContext.Rotas.Add(rota);
        return rota;
    }

    private Shift SeedShift(Rota rota, int dayOffset, bool isAllDay)
    {
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            RotaId = rota.Id,
            DayOffset = dayOffset,
            IsAllDay = isAllDay,
            StartTime = isAllDay ? LocalTime.Midnight : new LocalTime(10, 0),
            Duration = isAllDay ? Duration.FromHours(24) : Duration.FromHours(4),
            MinVolunteers = 2,
            MaxVolunteers = 5,
            CreatedAt = TestNow,
            UpdatedAt = TestNow,
            Rota = rota
        };
        _dbContext.Shifts.Add(shift);
        return shift;
    }
}
