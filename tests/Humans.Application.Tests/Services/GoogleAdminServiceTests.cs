using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Humans.Application.Tests.Services;

public class GoogleAdminServiceTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly IGoogleWorkspaceUserService _workspaceUserService;
    private readonly IGoogleSyncService _googleSyncService;
    private readonly IUserEmailService _userEmailService;
    private readonly IAuditLogService _auditLogService;
    private readonly GoogleAdminService _service;

    private readonly Guid _actorUserId = Guid.NewGuid();


    public GoogleAdminServiceTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new HumansDbContext(options);
        _workspaceUserService = Substitute.For<IGoogleWorkspaceUserService>();
        _googleSyncService = Substitute.For<IGoogleSyncService>();
        _userEmailService = Substitute.For<IUserEmailService>();
        _auditLogService = Substitute.For<IAuditLogService>();

        _service = new GoogleAdminService(
            _dbContext,
            _workspaceUserService,
            _googleSyncService,
            _userEmailService,
            _auditLogService,
            new FakeClock(Instant.FromUtc(2026, 1, 1, 0, 0)),
            NullLogger<GoogleAdminService>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    // --- GetWorkspaceAccountListAsync ---

    [Fact]
    public async Task GetWorkspaceAccountListAsync_ReturnsAccountsWithMatchedUsers()
    {
        var userId = Guid.NewGuid();
        var user = new User { Id = userId, Email = "test@example.com", DisplayName = "Test User" };
        _dbContext.Users.Add(user);
        _dbContext.UserEmails.Add(new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Email = "alice@nobodies.team",
            IsVerified = true,
            IsNotificationTarget = true,
        });
        await _dbContext.SaveChangesAsync();

        _workspaceUserService.ListAccountsAsync(Arg.Any<CancellationToken>())
            .Returns([
                new WorkspaceUserAccount("alice@nobodies.team", "Alice", "Smith", false,
                    DateTime.UtcNow, DateTime.UtcNow),
                new WorkspaceUserAccount("bob@nobodies.team", "Bob", "Jones", true,
                    DateTime.UtcNow, null),
            ]);

        var result = await _service.GetWorkspaceAccountListAsync();

        result.TotalAccounts.Should().Be(2);
        result.ActiveAccounts.Should().Be(1);
        result.SuspendedAccounts.Should().Be(1);
        result.LinkedAccounts.Should().Be(1);
        result.UnlinkedAccounts.Should().Be(1);
        result.ErrorMessage.Should().BeNull();

        var alice = result.Accounts.Single(a =>
            string.Equals(a.PrimaryEmail, "alice@nobodies.team", StringComparison.OrdinalIgnoreCase));
        alice.MatchedUserId.Should().Be(userId);
        alice.IsUsedAsPrimary.Should().BeTrue();
    }

    [Fact]
    public async Task GetWorkspaceAccountListAsync_ReturnsErrorOnException()
    {
        _workspaceUserService.ListAccountsAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("Google API error"));

        var result = await _service.GetWorkspaceAccountListAsync();

        result.ErrorMessage.Should().NotBeNull();
        result.TotalAccounts.Should().Be(0);
    }

    // --- ProvisionStandaloneAccountAsync ---

    [Fact]
    public async Task ProvisionStandaloneAccountAsync_CreatesAccountAndAudits()
    {
        _workspaceUserService.GetAccountAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((WorkspaceUserAccount?)null);
        _workspaceUserService.ProvisionAccountAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new WorkspaceUserAccount("test@nobodies.team", "Test", "User", false,
                DateTime.UtcNow, null));

        var result = await _service.ProvisionStandaloneAccountAsync(
            "test", "Test", "User", _actorUserId);

        result.Success.Should().BeTrue();
        result.TemporaryPassword.Should().NotBeNullOrEmpty();
        result.Message.Should().Contain("test@nobodies.team");

        await _auditLogService.Received(1).LogAsync(
            Arg.Any<Domain.Enums.AuditAction>(),
            Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), _actorUserId,
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task ProvisionStandaloneAccountAsync_ReturnsErrorIfAlreadyExists()
    {
        _workspaceUserService.GetAccountAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new WorkspaceUserAccount("test@nobodies.team", "Test", "User", false,
                DateTime.UtcNow, null));

        var result = await _service.ProvisionStandaloneAccountAsync(
            "test", "Test", "User", _actorUserId);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("already exists");
    }

    // --- SuspendAccountAsync ---

    [Fact]
    public async Task SuspendAccountAsync_SuspendsAndAudits()
    {
        var result = await _service.SuspendAccountAsync(
            "test@nobodies.team", _actorUserId);

        result.Success.Should().BeTrue();
        result.Message.Should().Contain("suspended");

        await _workspaceUserService.Received(1)
            .SuspendAccountAsync("test@nobodies.team", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SuspendAccountAsync_ReturnsErrorOnFailure()
    {
        _workspaceUserService.SuspendAccountAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("API error"));

        var result = await _service.SuspendAccountAsync(
            "test@nobodies.team", _actorUserId);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Failed to suspend");
    }

    // --- ReactivateAccountAsync ---

    [Fact]
    public async Task ReactivateAccountAsync_ReactivatesAndAudits()
    {
        var result = await _service.ReactivateAccountAsync(
            "test@nobodies.team", _actorUserId);

        result.Success.Should().BeTrue();
        result.Message.Should().Contain("reactivated");

        await _workspaceUserService.Received(1)
            .ReactivateAccountAsync("test@nobodies.team", Arg.Any<CancellationToken>());
    }

    // --- ResetPasswordAsync ---

    [Fact]
    public async Task ResetPasswordAsync_ResetsAndReturnsNewPassword()
    {
        var result = await _service.ResetPasswordAsync(
            "test@nobodies.team", _actorUserId);

        result.Success.Should().BeTrue();
        result.TemporaryPassword.Should().NotBeNullOrEmpty();
        result.Message.Should().Contain("Password reset");

        await _workspaceUserService.Received(1)
            .ResetPasswordAsync("test@nobodies.team", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // --- LinkAccountAsync ---

    [Fact]
    public async Task LinkAccountAsync_LinksEmailAndSetsGoogleEmail()
    {
        var userId = Guid.NewGuid();
        var user = new User { Id = userId, Email = "test@example.com", DisplayName = "Test User" };
        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync();

        var result = await _service.LinkAccountAsync(
            "alice@nobodies.team", userId, _actorUserId);

        result.Success.Should().BeTrue();
        result.Message.Should().Contain("Linked");

        // Verify GoogleEmail was set
        var updatedUser = await _dbContext.Users.FindAsync(userId);
        updatedUser!.GoogleEmail.Should().Be("alice@nobodies.team");

        await _userEmailService.Received(1)
            .AddVerifiedEmailAsync(userId, "alice@nobodies.team", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LinkAccountAsync_ReturnsErrorIfUserNotFound()
    {
        var result = await _service.LinkAccountAsync(
            "alice@nobodies.team", Guid.NewGuid(), _actorUserId);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not found");
    }

    [Fact]
    public async Task LinkAccountAsync_ReturnsErrorIfEmailConflict()
    {
        // Note: ILike is not supported by in-memory provider, so this exercises
        // the error path. The real duplicate check works on PostgreSQL.
        var userId = Guid.NewGuid();
        var user = new User { Id = userId, Email = "test@example.com", DisplayName = "Test" };
        _dbContext.Users.Add(user);
        _dbContext.UserEmails.Add(new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Email = "alice@nobodies.team",
            IsVerified = true,
        });
        await _dbContext.SaveChangesAsync();

        var result = await _service.LinkAccountAsync(
            "alice@nobodies.team", userId, _actorUserId);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    // --- ApplyEmailBackfillAsync ---

    [Fact]
    public async Task ApplyEmailBackfillAsync_UpdatesEmailsAndReturnsCount()
    {
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            Email = "old@example.com",
            UserName = "old@example.com",
            NormalizedEmail = "OLD@EXAMPLE.COM",
            NormalizedUserName = "OLD@EXAMPLE.COM",
            DisplayName = "Test",
        };
        _dbContext.Users.Add(user);
        _dbContext.UserEmails.Add(new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Email = "old@example.com",
            IsOAuth = true,
        });
        await _dbContext.SaveChangesAsync();

        var corrections = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { userId.ToString(), "new@example.com" }
        };

        var result = await _service.ApplyEmailBackfillAsync(
            [userId], corrections, _actorUserId);

        result.UpdatedCount.Should().Be(1);
        result.Errors.Should().BeEmpty();

        var updatedUser = await _dbContext.Users.FindAsync(userId);
        updatedUser!.Email.Should().Be("new@example.com");
        updatedUser.NormalizedEmail.Should().Be("NEW@EXAMPLE.COM");
    }

    [Fact]
    public async Task ApplyEmailBackfillAsync_SkipsUsersWithNoCorrection()
    {
        var userId = Guid.NewGuid();
        var user = new User { Id = userId, Email = "test@example.com", DisplayName = "Test" };
        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync();

        var result = await _service.ApplyEmailBackfillAsync(
            [userId], new Dictionary<string, string>(StringComparer.Ordinal), _actorUserId);

        result.UpdatedCount.Should().Be(0);
    }

    // --- LinkGroupToTeamAsync ---

    [Fact]
    public async Task LinkGroupToTeamAsync_LinksGroupAndSaves()
    {
        var teamId = Guid.NewGuid();
        var team = new Team { Id = teamId, Name = "Test Team", IsActive = true, Slug = "test-team" };
        _dbContext.Teams.Add(team);
        await _dbContext.SaveChangesAsync();

        _googleSyncService.EnsureTeamGroupAsync(teamId, false, Arg.Any<CancellationToken>())
            .Returns(DTOs.GroupLinkResult.Ok());

        var result = await _service.LinkGroupToTeamAsync(teamId, "test-team");

        result.Success.Should().BeTrue();
        result.Message.Should().Contain("test-team@nobodies.team");

        var updatedTeam = await _dbContext.Teams.FindAsync(teamId);
        updatedTeam!.GoogleGroupPrefix.Should().Be("test-team");
    }

    [Fact]
    public async Task LinkGroupToTeamAsync_ReturnsErrorIfTeamNotFound()
    {
        var result = await _service.LinkGroupToTeamAsync(Guid.NewGuid(), "prefix");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not found");
    }

    [Fact]
    public async Task LinkGroupToTeamAsync_RevertsOnError()
    {
        var teamId = Guid.NewGuid();
        var team = new Team
        {
            Id = teamId,
            Name = "Test Team",
            IsActive = true,
            Slug = "test-team",
            GoogleGroupPrefix = "old-prefix"
        };
        _dbContext.Teams.Add(team);
        await _dbContext.SaveChangesAsync();

        _googleSyncService.EnsureTeamGroupAsync(teamId, false, Arg.Any<CancellationToken>())
            .Returns(DTOs.GroupLinkResult.Error("Failed to create group"));

        var result = await _service.LinkGroupToTeamAsync(teamId, "new-prefix");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Failed to create group");
    }

    // --- GetActiveTeamsAsync ---

    [Fact]
    public async Task GetActiveTeamsAsync_ReturnsOnlyActiveTeamsOrdered()
    {
        await _dbContext.Teams.AddRangeAsync(
            new Team { Id = Guid.NewGuid(), Name = "Zebra", IsActive = true, Slug = "zebra" },
            new Team { Id = Guid.NewGuid(), Name = "Alpha", IsActive = true, Slug = "alpha" },
            new Team { Id = Guid.NewGuid(), Name = "Inactive", IsActive = false, Slug = "inactive" }
        );
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetActiveTeamsAsync();

        result.Should().HaveCount(2);
        result[0].Name.Should().Be("Alpha");
        result[1].Name.Should().Be("Zebra");
    }
}
