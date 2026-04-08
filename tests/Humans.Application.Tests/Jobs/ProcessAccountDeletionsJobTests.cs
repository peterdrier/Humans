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

public class ProcessAccountDeletionsJobTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly IEmailService _emailService;
    private readonly IAuditLogService _auditLogService;
    private readonly IProfileService _profileService;
    private readonly ITeamService _teamService;
    private readonly IMemoryCache _cache;
    private readonly HumansMetricsService _metrics;
    private readonly FakeClock _clock;
    private readonly ProcessAccountDeletionsJob _job;

    private static readonly Instant Now = Instant.FromUtc(2026, 3, 14, 12, 0);

    public ProcessAccountDeletionsJobTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new HumansDbContext(options);
        _emailService = Substitute.For<IEmailService>();
        _auditLogService = Substitute.For<IAuditLogService>();
        _profileService = Substitute.For<IProfileService>();
        _teamService = Substitute.For<ITeamService>();
        _cache = new MemoryCache(new MemoryCacheOptions());
        _clock = new FakeClock(Now);
        _metrics = new HumansMetricsService(
            Substitute.For<IServiceScopeFactory>(),
            Substitute.For<ILogger<HumansMetricsService>>());
        var logger = Substitute.For<ILogger<ProcessAccountDeletionsJob>>();

        _job = new ProcessAccountDeletionsJob(
            _dbContext, _emailService, _auditLogService,
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
    public async Task ExecuteAsync_AnonymizesUserWhenGracePeriodExpired()
    {
        var user = await SeedUserScheduledForDeletion(Now - Duration.FromDays(1));

        await _job.ExecuteAsync();

        var updated = await _dbContext.Users.Include(u => u.Profile).SingleAsync();
        updated.DisplayName.Should().Be("Deleted User");
        updated.Email.Should().StartWith("deleted-");
        updated.Email.Should().EndWith("@deleted.local");
        updated.ProfilePictureUrl.Should().BeNull();
        updated.PhoneNumber.Should().BeNull();
        updated.DeletionScheduledFor.Should().BeNull();
        updated.DeletionRequestedAt.Should().BeNull();
        updated.LockoutEnd.Should().Be(DateTimeOffset.MaxValue);
        updated.Profile!.FirstName.Should().Be("Deleted");
        updated.Profile.LastName.Should().Be("User");
        updated.Profile.Bio.Should().BeNull();
        updated.Profile.City.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_SkipsUserWhenGracePeriodNotExpired()
    {
        var user = await SeedUserScheduledForDeletion(Now + Duration.FromDays(5));

        await _job.ExecuteAsync();

        var updated = await _dbContext.Users.SingleAsync();
        updated.DisplayName.Should().Be("Test User");
        updated.DeletionScheduledFor.Should().NotBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_SkipsUserWithFutureEventHold()
    {
        // Grace period expired but event hold is still active
        var user = await SeedUserScheduledForDeletion(
            Now - Duration.FromDays(1),
            deletionEligibleAfter: Now + Duration.FromDays(10));

        await _job.ExecuteAsync();

        var updated = await _dbContext.Users.SingleAsync();
        updated.DisplayName.Should().Be("Test User");
    }

    [Fact]
    public async Task ExecuteAsync_ProcessesUserWhenEventHoldExpired()
    {
        var user = await SeedUserScheduledForDeletion(
            Now - Duration.FromDays(1),
            deletionEligibleAfter: Now - Duration.FromDays(1));

        await _job.ExecuteAsync();

        var updated = await _dbContext.Users.SingleAsync();
        updated.DisplayName.Should().Be("Deleted User");
    }

    [Fact]
    public async Task ExecuteAsync_SendsConfirmationEmail()
    {
        var user = await SeedUserScheduledForDeletion(Now - Duration.FromDays(1));

        await _job.ExecuteAsync();

        await _emailService.Received(1).SendAccountDeletedAsync(
            "test@example.com",
            "Test User",
            "en",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_LogsAuditEntry()
    {
        var user = await SeedUserScheduledForDeletion(Now - Duration.FromDays(1));

        await _job.ExecuteAsync();

        await _auditLogService.Received(1).LogAsync(
            AuditAction.AccountAnonymized,
            nameof(User),
            user.Id,
            Arg.Is<string>(s => s.Contains("Test User")),
            nameof(ProcessAccountDeletionsJob),
            Arg.Any<Guid?>(),
            Arg.Any<string?>());
    }

    [Fact]
    public async Task ExecuteAsync_RemovesContactFieldsAndVolunteerHistory()
    {
        var user = await SeedUserScheduledForDeletion(Now - Duration.FromDays(1));

        // Seed contact fields
        _dbContext.ContactFields.Add(new ContactField
        {
            Id = Guid.NewGuid(),
            ProfileId = user.Profile!.Id,
            FieldType = ContactFieldType.Phone,
            Value = "+1234567890",
            Visibility = ContactFieldVisibility.AllActiveProfiles
        });

        // Seed volunteer history
        _dbContext.VolunteerHistoryEntries.Add(new VolunteerHistoryEntry
        {
            Id = Guid.NewGuid(),
            ProfileId = user.Profile.Id,
            EventName = "Test Event",
            Description = "Testing"
        });
        await _dbContext.SaveChangesAsync();

        await _job.ExecuteAsync();

        var contactFields = await _dbContext.ContactFields
            .Where(cf => cf.ProfileId == user.Profile.Id)
            .CountAsync();
        contactFields.Should().Be(0);

        var historyEntries = await _dbContext.VolunteerHistoryEntries
            .Where(vh => vh.ProfileId == user.Profile.Id)
            .CountAsync();
        historyEntries.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_EndsActiveTeamMemberships()
    {
        var user = await SeedUserScheduledForDeletion(Now - Duration.FromDays(1));

        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = "Test Team",
            Slug = "test-team",
            CreatedAt = Now - Duration.FromDays(100)
        };
        _dbContext.Teams.Add(team);

        var membership = new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            UserId = user.Id,
            JoinedAt = Now - Duration.FromDays(50),
            LeftAt = null
        };
        _dbContext.TeamMembers.Add(membership);
        await _dbContext.SaveChangesAsync();

        // Re-read user so navigation property is populated
        _dbContext.Entry(user).State = EntityState.Detached;

        await _job.ExecuteAsync();

        var updatedMembership = await _dbContext.TeamMembers.SingleAsync();
        updatedMembership.LeftAt.Should().Be(Now);
    }

    [Fact]
    public async Task ExecuteAsync_EndsActiveRoleAssignments()
    {
        var user = await SeedUserScheduledForDeletion(Now - Duration.FromDays(1));

        var roleAssignment = new RoleAssignment
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            RoleName = "Admin",
            ValidFrom = Now - Duration.FromDays(30),
            ValidTo = null,
            CreatedByUserId = user.Id
        };
        _dbContext.RoleAssignments.Add(roleAssignment);
        await _dbContext.SaveChangesAsync();

        _dbContext.Entry(user).State = EntityState.Detached;

        await _job.ExecuteAsync();

        var updated = await _dbContext.RoleAssignments.SingleAsync();
        updated.ValidTo.Should().Be(Now);
    }

    [Fact]
    public async Task ExecuteAsync_CancelsActiveShiftSignups()
    {
        var user = await SeedUserScheduledForDeletion(Now - Duration.FromDays(1));

        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            RotaId = Guid.NewGuid(),
            StartTime = new LocalTime(10, 0),
            Duration = Duration.FromHours(4),
            MinVolunteers = 1,
            MaxVolunteers = 10
        };
        _dbContext.Shifts.Add(shift);

        var signup = new ShiftSignup
        {
            Id = Guid.NewGuid(),
            ShiftId = shift.Id,
            UserId = user.Id,
            Status = SignupStatus.Confirmed
        };
        _dbContext.ShiftSignups.Add(signup);
        await _dbContext.SaveChangesAsync();

        _dbContext.Entry(user).State = EntityState.Detached;

        await _job.ExecuteAsync();

        var updatedSignup = await _dbContext.ShiftSignups.SingleAsync();
        updatedSignup.Status.Should().Be(SignupStatus.Cancelled);
        updatedSignup.StatusReason.Should().Be("Account deletion");
    }

    [Fact]
    public async Task ExecuteAsync_RemovesUserEmails()
    {
        var user = await SeedUserScheduledForDeletion(Now - Duration.FromDays(1));

        _dbContext.UserEmails.Add(new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Email = "secondary@example.com",
            IsVerified = true,
            IsNotificationTarget = false
        });
        await _dbContext.SaveChangesAsync();

        _dbContext.Entry(user).State = EntityState.Detached;

        await _job.ExecuteAsync();

        var emailCount = await _dbContext.UserEmails.Where(e => e.UserId == user.Id).CountAsync();
        emailCount.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_InvalidatesCaches()
    {
        var user = await SeedUserScheduledForDeletion(Now - Duration.FromDays(1));

        await _job.ExecuteAsync();

        _profileService.Received(1).UpdateProfileCache(user.Id, null);
        _teamService.Received(1).RemoveMemberFromAllTeamsCache(user.Id);
    }

    [Fact]
    public async Task ExecuteAsync_NoUsersToDelete_DoesNothing()
    {
        // No users in DB at all
        await _job.ExecuteAsync();

        await _emailService.DidNotReceive().SendAccountDeletedAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ContinuesProcessingAfterIndividualFailure()
    {
        var user1 = await SeedUserScheduledForDeletion(Now - Duration.FromDays(1));

        // Create a second user
        var user2Id = Guid.NewGuid();
        var user2 = new User
        {
            Id = user2Id,
            UserName = "user2",
            NormalizedUserName = "USER2",
            Email = "user2@example.com",
            NormalizedEmail = "USER2@EXAMPLE.COM",
            DisplayName = "User Two",
            DeletionRequestedAt = Now - Duration.FromDays(31),
            DeletionScheduledFor = Now - Duration.FromDays(1),
            PreferredLanguage = "en",
            Profile = new Profile
            {
                Id = Guid.NewGuid(),
                UserId = user2Id,
                FirstName = "User",
                LastName = "Two",
                BurnerName = "UTwo"
            }
        };
        _dbContext.Users.Add(user2);
        await _dbContext.SaveChangesAsync();

        // Make audit log throw for first user to simulate failure
        _auditLogService.LogAsync(
            Arg.Any<AuditAction>(), Arg.Any<string>(), user1.Id,
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<Guid?>(), Arg.Any<string?>())
            .Returns(Task.FromException(new InvalidOperationException("DB error")));

        _auditLogService.LogAsync(
            Arg.Any<AuditAction>(), Arg.Any<string>(), user2Id,
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<Guid?>(), Arg.Any<string?>())
            .Returns(Task.CompletedTask);

        await _job.ExecuteAsync();

        // Second user should still be processed (anonymized)
        var updatedUser2 = await _dbContext.Users.SingleAsync(u => u.Id == user2Id);
        updatedUser2.DisplayName.Should().Be("Deleted User");
    }

    private async Task<User> SeedUserScheduledForDeletion(
        Instant deletionScheduledFor,
        Instant? deletionEligibleAfter = null)
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
            DeletionRequestedAt = deletionScheduledFor - Duration.FromDays(30),
            DeletionScheduledFor = deletionScheduledFor,
            DeletionEligibleAfter = deletionEligibleAfter,
            PreferredLanguage = "en",
            Profile = new Profile
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                FirstName = "Test",
                LastName = "User",
                BurnerName = "Tester",
                Bio = "A test bio",
                City = "Madrid"
            }
        };

        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync();
        return user;
    }
}
