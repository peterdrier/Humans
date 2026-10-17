using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Users.Controllers;
using Humans.Users.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Humans.Users.Contracts;
using Humans.Users.Data;
using Humans.Users.Data.Repositories;
using Humans.Users.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Users.Tests.Services;

/// <summary>The admin name-backfill screen's service.</summary>
public sealed class UserNameSyncServiceTests : IDisposable
{
    private readonly UsersDbContext _dbContext;
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 3, 1, 12, 0));
    private readonly UserRepository _repo;
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();
    private readonly Guid _actorId = Guid.NewGuid();
    private readonly UserNameSyncService _service;

    public UserNameSyncServiceTests()
    {
        var options = new DbContextOptionsBuilder<UsersDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new UsersDbContext(options);
        _repo = new UserRepository(new TestDbContextFactory(options), _clock);

        var userService = Substitute.For<IUserManagement>();
        userService.GetAllUserInfosAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<UserInfo>>([]));

        _service = new UserNameSyncService(_repo, userService, _audit);
    }

    public void Dispose() => _dbContext.Dispose();

    [HumansFact]
    public async Task Profile_confirm_without_actor_refuses_before_backfill()
    {
        var users = Substitute.For<IUserManagement>();
        var controller = new ProfileBackfillAdminController(users,
            NullLogger<ProfileBackfillAdminController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        (await controller.Run(Xunit.TestContext.Current.CancellationToken)).Should().BeOfType<UnauthorizedResult>();
        users.ReceivedCalls().Should().BeEmpty();
    }

    [HumansFact]
    public async Task Confirm_without_actor_refuses_before_backfill()
    {
        var sync = Substitute.For<IUserNameSyncService>();
        var controller = new UserNameBackfillAdminController(Substitute.For<IUserManagement>(), sync,
            NullLogger<UserNameBackfillAdminController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        (await controller.Run(Xunit.TestContext.Current.CancellationToken)).Should().BeOfType<UnauthorizedResult>();
        sync.ReceivedCalls().Should().BeEmpty();
    }

    [HumansFact]
    public async Task Reports_a_human_whose_User_row_has_no_names_yet()
    {
        var userId = await SeedLegacyPairAsync("Sparkle", "Ada", "Lovelace");

        var unsynced = await _service.GetUnsyncedAsync(Xunit.TestContext.Current.CancellationToken);

        unsynced.Should().ContainSingle();
        unsynced[0].UserId.Should().Be(userId);
        unsynced[0].ProfileBurnerName.Should().Be("Sparkle");
        unsynced[0].ProfileLegalName.Should().Be("Ada Lovelace");
        unsynced[0].BurnerNameMissing.Should().BeTrue();
        unsynced[0].LegalNameMissing.Should().BeTrue();
    }

    [HumansFact]
    public async Task Sync_copies_the_names_and_is_idempotent()
    {
        var userId = await SeedLegacyPairAsync("Sparkle", "Ada", "Lovelace");

        _audit.LogAsync(AuditAction.UserNamesBackfilled, nameof(User), userId,
            Arg.Any<string>(), _actorId, null, null).Returns(async _ =>
            {
                var saved = await _dbContext.Users.AsNoTracking().SingleAsync(u => u.Id == userId,
                    Xunit.TestContext.Current.CancellationToken);
                saved.BurnerName.Should().Be("Sparkle");
            });
        var synced = await _service.SyncAllAsync(_actorId, Xunit.TestContext.Current.CancellationToken);
        synced.Should().Be(1);

        _dbContext.ChangeTracker.Clear();
        var user = await _dbContext.Users.AsNoTracking()
            .SingleAsync(u => u.Id == userId, Xunit.TestContext.Current.CancellationToken);
        user.BurnerName.Should().Be("Sparkle");
        user.FirstName.Should().Be("Ada");
        user.LastName.Should().Be("Lovelace");

        await _audit.Received(1).LogAsync(AuditAction.UserNamesBackfilled, nameof(User), userId,
            Arg.Is<string>(description => !description.Contains("Lovelace", StringComparison.Ordinal)),
            _actorId, null, null);

        // Re-running reports nothing left and moves nothing.
        (await _service.GetUnsyncedAsync(Xunit.TestContext.Current.CancellationToken)).Should().BeEmpty();
        (await _service.SyncAllAsync(_actorId, Xunit.TestContext.Current.CancellationToken)).Should().Be(0);
        _audit.ReceivedCalls().Should().HaveCount(1);
    }

    [HumansFact]
    public async Task Profileless_humans_are_never_reported()
    {
        _dbContext.Users.Add(NewUser());
        await _dbContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        (await _service.GetUnsyncedAsync(Xunit.TestContext.Current.CancellationToken)).Should().BeEmpty();
    }

    /// <summary>A pre-#1097 row: names on the Profile only.</summary>
    private async Task<Guid> SeedLegacyPairAsync(string burnerName, string firstName, string lastName)
    {
        var user = NewUser();
        var now = _clock.GetCurrentInstant();
        _dbContext.Users.Add(user);
        _dbContext.Profiles.Add(new Profile
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            BurnerName = burnerName,
            FirstName = firstName,
            LastName = lastName,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await _dbContext.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        _dbContext.ChangeTracker.Clear();
        return user.Id;
    }

    private User NewUser() => new()
    {
        Id = Guid.NewGuid(),
        UserName = $"user-{Guid.NewGuid():N}@example.com",
        Email = $"user-{Guid.NewGuid():N}@example.com",
        DisplayName = "Seeded User",
        CreatedAt = _clock.GetCurrentInstant(),
    };
}
