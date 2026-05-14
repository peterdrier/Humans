using AwesomeAssertions;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Services.Shifts;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Constants;
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
/// Covers <c>Shifts.md</c> trigger line 259:
/// "When a Bail or Remove drops the confirmed count below MinVolunteers,
/// a ShiftCoverageGap actionable notification (priority High) is sent to
/// the department's coordinators."
///
/// Lives in its own file so the notification mock can be captured as a field
/// without disturbing the monster-sized <see cref="ShiftSignupServiceTests"/>.
/// </summary>
public class ShiftSignupServiceCoverageGapTests : IDisposable
{
    private static readonly Instant TestNow = Instant.FromUtc(2026, 6, 15, 12, 0);

    private readonly HumansDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly INotificationService _notificationService;
    private readonly ITeamService _teamService;
    private readonly ShiftSignupService _service;

    public ShiftSignupServiceCoverageGapTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new HumansDbContext(options);
        _clock = new FakeClock(TestNow);
        _notificationService = Substitute.For<INotificationService>();
        _teamService = Substitute.For<ITeamService>();
        var auditLog = Substitute.For<IAuditLogService>();
        var roleAssignmentService = Substitute.For<IRoleAssignmentService>();

        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(ITeamService)).Returns(_teamService);
        serviceProvider.GetService(typeof(IRoleAssignmentService)).Returns(roleAssignmentService);

        var shiftRepo = new ShiftManagementRepository(new TestDbContextFactory(options));
        var shiftMgmt = new ShiftManagementService(
            shiftRepo,
            auditLog,
            Substitute.For<IAdminAuthorizationService>(),
            serviceProvider,
            new MemoryCache(new MemoryCacheOptions()),
            _clock,
            NullLogger<ShiftManagementService>.Instance);

        var signupRepo = new ShiftSignupRepository(_dbContext, _clock);
        var membership = Substitute.For<IMembershipCalculator>();
        membership.HasAllRequiredConsentsForTeamAsync(
                Arg.Any<Guid>(), SystemTeamIds.Volunteers, Arg.Any<CancellationToken>())
            .Returns(true);

        _service = new ShiftSignupService(
            signupRepo,
            shiftMgmt,
            membership,
            auditLog,
            _notificationService,
            Substitute.For<IAdminAuthorizationService>(),
            serviceProvider,
            _clock,
            NullLogger<ShiftSignupService>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [HumansFact]
    public async Task BailAsync_DropsConfirmedBelowMinVolunteers_SendsShiftCoverageGapNotification()
    {
        // Arrange: shift with MinVolunteers=2, exactly 2 Confirmed signups; bail
        // by either drops it to 1 (below min).
        var (es, rota, shift, userA, userB) = await SeedScenarioAsync(
            minVolunteers: 2, maxVolunteers: 5);
        var coordinatorId = Guid.NewGuid();
        _teamService.GetCoordinatorUserIdsAsync(rota.TeamId, Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { coordinatorId });

        var signupA = await _dbContext.ShiftSignups.FirstAsync(s => s.UserId == userA);

        // Act
        var result = await _service.BailAsync(signupA.Id, userA, reason: null);
        result.Success.Should().BeTrue();

        // Assert: an actionable High-priority ShiftCoverageGap notification was
        // sent to the department coordinators.
        await _notificationService.Received(1).SendAsync(
            NotificationSource.ShiftCoverageGap,
            NotificationClass.Actionable,
            NotificationPriority.High,
            Arg.Any<string>(),
            Arg.Is<IReadOnlyList<Guid>>(c => c.Contains(coordinatorId)),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task BailAsync_LeavesConfirmedAtOrAboveMinVolunteers_DoesNotSendShiftCoverageGap()
    {
        // Arrange: shift with MinVolunteers=1, 2 Confirmed signups; one bail
        // leaves 1 (still at min) — coverage gap should NOT fire.
        var (es, rota, shift, userA, userB) = await SeedScenarioAsync(
            minVolunteers: 1, maxVolunteers: 5);
        var coordinatorId = Guid.NewGuid();
        _teamService.GetCoordinatorUserIdsAsync(rota.TeamId, Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { coordinatorId });

        var signupA = await _dbContext.ShiftSignups.FirstAsync(s => s.UserId == userA);

        // Act
        var result = await _service.BailAsync(signupA.Id, userA, reason: null);
        result.Success.Should().BeTrue();

        // Assert: no ShiftCoverageGap notification (confirmed count 1 >= min 1).
        await _notificationService.DidNotReceive().SendAsync(
            NotificationSource.ShiftCoverageGap,
            Arg.Any<NotificationClass>(),
            Arg.Any<NotificationPriority>(),
            Arg.Any<string>(),
            Arg.Any<IReadOnlyList<Guid>>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    private async Task<(EventSettings es, Rota rota, Shift shift, Guid userA, Guid userB)>
        SeedScenarioAsync(int minVolunteers, int maxVolunteers)
    {
        var es = new EventSettings
        {
            Id = Guid.NewGuid(),
            EventName = "CoverageGap Event",
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
        _dbContext.EventSettings.Add(es);

        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = "Dept",
            Slug = "dept",
            SystemTeamType = SystemTeamType.None,
            ParentTeamId = null,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        };
        _dbContext.Teams.Add(team);

        var rota = new Rota
        {
            Id = Guid.NewGuid(),
            EventSettingsId = es.Id,
            TeamId = team.Id,
            Name = "Coverage Rota",
            Priority = ShiftPriority.Normal,
            Policy = SignupPolicy.Public,
            Period = RotaPeriod.Event,
            CreatedAt = TestNow,
            UpdatedAt = TestNow,
            EventSettings = es
        };
        _dbContext.Rotas.Add(rota);

        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            RotaId = rota.Id,
            DayOffset = 1,
            StartTime = new LocalTime(10, 0),
            Duration = Duration.FromHours(4),
            MinVolunteers = minVolunteers,
            MaxVolunteers = maxVolunteers,
            CreatedAt = TestNow,
            UpdatedAt = TestNow,
            Rota = rota
        };
        _dbContext.Shifts.Add(shift);

        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        _dbContext.ShiftSignups.Add(new ShiftSignup
        {
            Id = Guid.NewGuid(),
            ShiftId = shift.Id,
            UserId = userA,
            Status = SignupStatus.Confirmed,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        });
        _dbContext.ShiftSignups.Add(new ShiftSignup
        {
            Id = Guid.NewGuid(),
            ShiftId = shift.Id,
            UserId = userB,
            Status = SignupStatus.Confirmed,
            CreatedAt = TestNow,
            UpdatedAt = TestNow
        });

        await _dbContext.SaveChangesAsync();
        return (es, rota, shift, userA, userB);
    }
}
