using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Jobs;
using Humans.Infrastructure.Services;
using Xunit;

namespace Humans.Application.Tests.Jobs;

public class SuspendNonCompliantMembersJobTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly IMembershipCalculator _membershipCalculator;
    private readonly IEmailService _emailService;
    private readonly INotificationService _notificationService;
    private readonly IGoogleSyncService _googleSyncService;
    private readonly IAuditLogService _auditLogService;
    private readonly IProfileService _profileService;
    private readonly ITeamService _teamService;
    private readonly IMemoryCache _cache;
    private readonly HumansMetricsService _metrics;
    private readonly FakeClock _clock;
    private readonly SuspendNonCompliantMembersJob _job;

    private static readonly Instant Now = Instant.FromUtc(2026, 3, 14, 12, 0);

    public SuspendNonCompliantMembersJobTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new HumansDbContext(options);
        _membershipCalculator = Substitute.For<IMembershipCalculator>();
        _emailService = Substitute.For<IEmailService>();
        _notificationService = Substitute.For<INotificationService>();
        _googleSyncService = Substitute.For<IGoogleSyncService>();
        _auditLogService = Substitute.For<IAuditLogService>();
        _profileService = Substitute.For<IProfileService>();
        _teamService = Substitute.For<ITeamService>();
        _cache = new MemoryCache(new MemoryCacheOptions());
        _clock = new FakeClock(Now);
        _metrics = new HumansMetricsService(
            Substitute.For<IServiceScopeFactory>(),
            Substitute.For<ILogger<HumansMetricsService>>());
        var logger = Substitute.For<ILogger<SuspendNonCompliantMembersJob>>();

        _job = new SuspendNonCompliantMembersJob(
            _dbContext, _membershipCalculator, _emailService,
            _notificationService, _googleSyncService, _auditLogService,
            _profileService, _teamService, _cache, _metrics, logger, _clock);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _cache.Dispose();
        _metrics.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ExecuteAsync_SuspendsNonCompliantUser()
    {
        var user = await SeedUserWithProfile();
        _membershipCalculator.GetUsersRequiringStatusUpdateAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { user.Id });

        await _job.ExecuteAsync();

        var updated = await _dbContext.Users.Include(u => u.Profile).SingleAsync();
        updated.Profile!.IsSuspended.Should().BeTrue();
        updated.Profile.UpdatedAt.Should().Be(Now);
    }

    [Fact]
    public async Task ExecuteAsync_NoUsersToSuspend_DoesNothing()
    {
        _membershipCalculator.GetUsersRequiringStatusUpdateAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Guid>());

        await _job.ExecuteAsync();

        await _emailService.DidNotReceive().SendAccessSuspendedAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_SkipsAlreadySuspendedUsers()
    {
        var user = await SeedUserWithProfile(isSuspended: true);
        _membershipCalculator.GetUsersRequiringStatusUpdateAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { user.Id });

        await _job.ExecuteAsync();

        await _emailService.DidNotReceive().SendAccessSuspendedAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_SkipsUsersWithoutProfile()
    {
        var userId = Guid.NewGuid();
        _dbContext.Users.Add(new User
        {
            Id = userId,
            UserName = "noprofile",
            NormalizedUserName = "NOPROFILE",
            Email = "noprofile@example.com",
            NormalizedEmail = "NOPROFILE@EXAMPLE.COM",
            DisplayName = "No Profile",
            PreferredLanguage = "en"
        });
        await _dbContext.SaveChangesAsync();

        _membershipCalculator.GetUsersRequiringStatusUpdateAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { userId });

        await _job.ExecuteAsync();

        await _emailService.DidNotReceive().SendAccessSuspendedAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_SendsSuspensionEmail()
    {
        var user = await SeedUserWithProfile();
        _membershipCalculator.GetUsersRequiringStatusUpdateAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { user.Id });

        await _job.ExecuteAsync();

        await _emailService.Received(1).SendAccessSuspendedAsync(
            "test@example.com",
            "Test User",
            Arg.Is<string>(s => s.Contains("consent")),
            "en",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_SendsInAppNotification()
    {
        var user = await SeedUserWithProfile();
        _membershipCalculator.GetUsersRequiringStatusUpdateAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { user.Id });

        await _job.ExecuteAsync();

        await _notificationService.Received(1).SendAsync(
            NotificationSource.AccessSuspended,
            NotificationClass.Actionable,
            NotificationPriority.Critical,
            Arg.Any<string>(),
            Arg.Is<IReadOnlyList<Guid>>(ids => ids.Contains(user.Id)),
            body: Arg.Any<string?>(),
            actionUrl: "/Legal/Consent",
            actionLabel: Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_RemovesFromTeamResources()
    {
        var user = await SeedUserWithProfile();
        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = "Test Team",
            Slug = "test-team",
            CreatedAt = Now - Duration.FromDays(100)
        };
        _dbContext.Teams.Add(team);

        _dbContext.TeamMembers.Add(new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            UserId = user.Id,
            JoinedAt = Now - Duration.FromDays(50),
            LeftAt = null
        });
        await _dbContext.SaveChangesAsync();

        _membershipCalculator.GetUsersRequiringStatusUpdateAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { user.Id });

        await _job.ExecuteAsync();

        await _googleSyncService.Received(1).RemoveUserFromTeamResourcesAsync(
            team.Id, user.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_LogsAuditEntry()
    {
        var user = await SeedUserWithProfile();
        _membershipCalculator.GetUsersRequiringStatusUpdateAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { user.Id });

        await _job.ExecuteAsync();

        await _auditLogService.Received(1).LogAsync(
            AuditAction.MemberSuspended,
            nameof(User),
            user.Id,
            Arg.Is<string>(s => s.Contains("Test User")),
            nameof(SuspendNonCompliantMembersJob),
            Arg.Any<Guid?>(),
            Arg.Any<string?>());
    }

    [Fact]
    public async Task ExecuteAsync_InvalidatesCaches()
    {
        var user = await SeedUserWithProfile();
        _membershipCalculator.GetUsersRequiringStatusUpdateAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { user.Id });

        await _job.ExecuteAsync();

        await _profileService.Received(1).InvalidateCacheAsync(user.Id, Arg.Any<CancellationToken>());
        _teamService.Received(1).RemoveMemberFromAllTeamsCache(user.Id);
    }

    [Fact]
    public async Task ExecuteAsync_ContinuesWhenGoogleSyncFails()
    {
        var user = await SeedUserWithProfile();
        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = "Test Team",
            Slug = "test-team",
            CreatedAt = Now - Duration.FromDays(100)
        };
        _dbContext.Teams.Add(team);

        _dbContext.TeamMembers.Add(new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            UserId = user.Id,
            JoinedAt = Now - Duration.FromDays(50),
            LeftAt = null
        });
        await _dbContext.SaveChangesAsync();

        _membershipCalculator.GetUsersRequiringStatusUpdateAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { user.Id });

        _googleSyncService.RemoveUserFromTeamResourcesAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("Google API error")));

        // Should not throw — Google sync failures are caught and logged
        await _job.ExecuteAsync();

        // User should still be suspended despite Google sync failure
        var updated = await _dbContext.Users.Include(u => u.Profile).SingleAsync();
        updated.Profile!.IsSuspended.Should().BeTrue();
    }

    private async Task<User> SeedUserWithProfile(bool isSuspended = false)
    {
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            UserName = "testuser",
            NormalizedUserName = "TESTUSER",
            Email = "test@example.com",
            NormalizedEmail = "TEST@EXAMPLE.COM",
            DisplayName = "Test User",
            PreferredLanguage = "en",
            Profile = new Profile
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                FirstName = "Test",
                LastName = "User",
                BurnerName = "Tester",
                IsSuspended = isSuspended,
                UpdatedAt = Now - Duration.FromDays(10)
            }
        };

        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync();
        return user;
    }
}
