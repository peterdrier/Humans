using AwesomeAssertions;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;
using GoogleAdminService = Humans.Application.Services.GoogleIntegration.GoogleAdminService;

namespace Humans.Application.Tests.Services;

/// <summary>
/// Unit tests for the migrated Google Integration
/// <see cref="GoogleAdminService"/>. After the §15 migration the service has
/// no DbContext dependency — cross-section data access goes through
/// <see cref="IUserService"/>, <see cref="IUserEmailService"/>,
/// <see cref="ITeamService"/>, and <see cref="ITeamResourceService"/>. These
/// tests pin down both the Google Workspace orchestration contract and the
/// owning-service delegation boundary.
/// </summary>
public class GoogleAdminServiceTests
{
    private readonly IGoogleWorkspaceUserService _workspaceUserService;
    private readonly IGoogleSyncService _googleSyncService;
    private readonly ITeamService _teamService;
    private readonly ITeamResourceService _teamResourceService;
    private readonly IUserService _userService;
    private readonly IUserEmailService _userEmailService;
    private readonly IAuditLogService _auditLogService;
    private readonly GoogleAdminService _service;

    private readonly Guid _actorUserId = Guid.NewGuid();

    public GoogleAdminServiceTests()
    {
        _workspaceUserService = Substitute.For<IGoogleWorkspaceUserService>();
        _googleSyncService = Substitute.For<IGoogleSyncService>();
        _teamService = Substitute.For<ITeamService>();
        _teamResourceService = Substitute.For<ITeamResourceService>();
        _userService = Substitute.For<IUserService>();
        _userEmailService = Substitute.For<IUserEmailService>();
        _auditLogService = Substitute.For<IAuditLogService>();

        _teamResourceService.GetActiveResourceCountsByTeamAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int>());

        _service = new GoogleAdminService(
            _workspaceUserService,
            _googleSyncService,
            _teamService,
            _teamResourceService,
            _userService,
            _userEmailService,
            _auditLogService,
            NullLogger<GoogleAdminService>.Instance);
    }

    // --- GetWorkspaceAccountListAsync ---

    [HumansFact]
    public async Task GetWorkspaceAccountListAsync_ReturnsAccountsWithMatchedUsers()
    {
        var userId = Guid.NewGuid();
        _workspaceUserService.ListAccountsAsync(Arg.Any<CancellationToken>())
            .Returns([
                new WorkspaceUserAccount("alice@nobodies.team", "Alice", "Smith", false,
                    DateTime.UtcNow, DateTime.UtcNow, IsEnrolledIn2Sv: true),
                new WorkspaceUserAccount("bob@nobodies.team", "Bob", "Jones", true,
                    DateTime.UtcNow, null, IsEnrolledIn2Sv: false),
            ]);

        _userEmailService.MatchByEmailsAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns([
                new UserEmailMatch(
                    "alice@nobodies.team",
                    userId,
                    IsNotificationTarget: true,
                    IsVerified: true,
                    UpdatedAt: NodaTime.SystemClock.Instance.GetCurrentInstant())
            ]);

        _userService.GetByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, User>
            {
                [userId] = new User { Id = userId, DisplayName = "Test User" }
            });

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

    [HumansFact]
    public async Task GetWorkspaceAccountListAsync_PicksVerifiedWinner_WhenDuplicateEmailMatches()
    {
        // user_emails may hold both a verified and unverified row for the
        // same address. MatchByEmailsAsync surfaces both; the service must
        // collapse them rather than throw on the duplicate key.
        var verifiedUserId = Guid.NewGuid();
        var unverifiedUserId = Guid.NewGuid();
        var now = NodaTime.SystemClock.Instance.GetCurrentInstant();

        _workspaceUserService.ListAccountsAsync(Arg.Any<CancellationToken>())
            .Returns([
                new WorkspaceUserAccount("dup@nobodies.team", "Dup", "User", false,
                    DateTime.UtcNow, null, IsEnrolledIn2Sv: false),
            ]);

        _userEmailService.MatchByEmailsAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns([
                // Unverified row is newer but must lose to the verified row.
                new UserEmailMatch(
                    "dup@nobodies.team", unverifiedUserId,
                    IsNotificationTarget: false,
                    IsVerified: false,
                    UpdatedAt: now),
                new UserEmailMatch(
                    "dup@nobodies.team", verifiedUserId,
                    IsNotificationTarget: true,
                    IsVerified: true,
                    UpdatedAt: now - NodaTime.Duration.FromHours(1)),
            ]);

        _userService.GetByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, User>
            {
                [verifiedUserId] = new User { Id = verifiedUserId, DisplayName = "Verified User" }
            });

        var result = await _service.GetWorkspaceAccountListAsync();

        result.ErrorMessage.Should().BeNull();
        result.TotalAccounts.Should().Be(1);
        var dup = result.Accounts.Single();
        dup.MatchedUserId.Should().Be(verifiedUserId);
        dup.IsUsedAsPrimary.Should().BeTrue();
    }

    [HumansFact]
    public async Task GetWorkspaceAccountListAsync_ReturnsErrorOnException()
    {
        _workspaceUserService.ListAccountsAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Google API error"));

        var result = await _service.GetWorkspaceAccountListAsync();

        result.ErrorMessage.Should().NotBeNull();
        result.TotalAccounts.Should().Be(0);
    }

    // --- ProvisionStandaloneAccountAsync ---

    [HumansFact]
    public async Task ProvisionStandaloneAccountAsync_CreatesAccountAndAudits()
    {
        _workspaceUserService.GetAccountAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((WorkspaceUserAccount?)null);
        _workspaceUserService.ProvisionAccountAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new WorkspaceUserAccount("test@nobodies.team", "Test", "User", false,
                DateTime.UtcNow, null, IsEnrolledIn2Sv: false));

        var result = await _service.ProvisionStandaloneAccountAsync(
            "test", "Test", "User", _actorUserId);

        result.Success.Should().BeTrue();
        result.TemporaryPassword.Should().NotBeNullOrEmpty();
        result.Message.Should().Contain("test@nobodies.team");

        await _auditLogService.Received(1).LogAsync(
            Arg.Any<AuditAction>(),
            Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), _actorUserId,
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task ProvisionStandaloneAccountAsync_ReturnsErrorIfAlreadyExists()
    {
        _workspaceUserService.GetAccountAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new WorkspaceUserAccount("test@nobodies.team", "Test", "User", false,
                DateTime.UtcNow, null, IsEnrolledIn2Sv: false));

        var result = await _service.ProvisionStandaloneAccountAsync(
            "test", "Test", "User", _actorUserId);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("already exists in Google Workspace");
    }

    [HumansFact]
    public async Task ProvisionStandaloneAccountAsync_RejectsWhenPrefixInUseByUserEmail()
    {
        _userEmailService.IsEmailLinkedToAnyUserAsync(
                "test@nobodies.team", Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _service.ProvisionStandaloneAccountAsync(
            "test", "Test", "User", _actorUserId);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("already in use by another human");

        // No Workspace calls, no audit write
        await _workspaceUserService.DidNotReceive().GetAccountAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _workspaceUserService.DidNotReceive().ProvisionAccountAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _auditLogService.DidNotReceive().LogAsync(
            Arg.Any<Domain.Enums.AuditAction>(),
            Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task ProvisionStandaloneAccountAsync_RejectsWhenPrefixInUseByGoogleEmail()
    {
        var ownerId = Guid.NewGuid();
        _userEmailService.IsEmailLinkedToAnyUserAsync(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);
        _userService.GetByEmailOrAlternateAsync(
                "test@nobodies.team", Arg.Any<CancellationToken>())
            .Returns(new User
            {
                Id = ownerId,
                Email = "x@example.com",
                DisplayName = "X",
                GoogleEmail = "test@nobodies.team",
            });

        var result = await _service.ProvisionStandaloneAccountAsync(
            "test", "Test", "User", _actorUserId);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("already in use by another human");

        await _workspaceUserService.DidNotReceive().GetAccountAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _workspaceUserService.DidNotReceive().ProvisionAccountAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ProvisionStandaloneAccountAsync_RejectsWhenPrefixCollidesWithTeamGoogleGroup()
    {
        _userEmailService.IsEmailLinkedToAnyUserAsync(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);
        _userService.GetByEmailOrAlternateAsync(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((User?)null);
        _teamService.GetTeamNameByGoogleGroupPrefixAsync(
                "comms", Arg.Any<CancellationToken>())
            .Returns("Communications");

        var result = await _service.ProvisionStandaloneAccountAsync(
            "comms", "Any", "Name", _actorUserId);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Google Group");
        result.ErrorMessage.Should().Contain("Communications");

        await _workspaceUserService.DidNotReceive().GetAccountAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _workspaceUserService.DidNotReceive().ProvisionAccountAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // --- SuspendAccountAsync ---

    [HumansFact]
    public async Task SuspendAccountAsync_SuspendsAndAudits()
    {
        var result = await _service.SuspendAccountAsync(
            "test@nobodies.team", _actorUserId);

        result.Success.Should().BeTrue();
        result.Message.Should().Contain("suspended");

        await _workspaceUserService.Received(1)
            .SuspendAccountAsync("test@nobodies.team", Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SuspendAccountAsync_ReturnsErrorOnFailure()
    {
        _workspaceUserService.SuspendAccountAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("API error"));

        var result = await _service.SuspendAccountAsync(
            "test@nobodies.team", _actorUserId);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Failed to suspend");
    }

    // --- ReactivateAccountAsync ---

    [HumansFact]
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

    [HumansFact]
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

    [HumansFact]
    public async Task LinkAccountAsync_LinksEmailAndSetsGoogleEmail()
    {
        var userId = Guid.NewGuid();
        _userService.GetByIdAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new User { Id = userId, DisplayName = "Test User" });
        _userEmailService.IsEmailLinkedToAnyUserAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);
        _userService.SetGoogleEmailAsync(userId, "alice@nobodies.team", Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _service.LinkAccountAsync(
            "alice@nobodies.team", userId, _actorUserId);

        result.Success.Should().BeTrue();
        result.Message.Should().Contain("Linked");

        await _userEmailService.Received(1)
            .AddVerifiedEmailAsync(userId, "alice@nobodies.team", Arg.Any<CancellationToken>());
        await _userService.Received(1)
            .SetGoogleEmailAsync(userId, "alice@nobodies.team", Arg.Any<CancellationToken>());
        await _teamService.Received(1)
            .EnqueueGoogleResyncForUserTeamsAsync(userId, Arg.Any<CancellationToken>());
        await _auditLogService.Received(1).LogAsync(
            AuditAction.WorkspaceAccountLinked,
            "WorkspaceAccount", userId,
            Arg.Any<string>(), _actorUserId,
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task LinkAccountAsync_ReturnsErrorIfUserNotFound()
    {
        _userService.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((User?)null);

        var result = await _service.LinkAccountAsync(
            "alice@nobodies.team", Guid.NewGuid(), _actorUserId);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not found");
    }

    [HumansFact]
    public async Task LinkAccountAsync_ReturnsErrorIfEmailConflict()
    {
        var userId = Guid.NewGuid();
        _userService.GetByIdAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new User { Id = userId, DisplayName = "Test" });
        _userEmailService.IsEmailLinkedToAnyUserAsync(
                "alice@nobodies.team", Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _service.LinkAccountAsync(
            "alice@nobodies.team", userId, _actorUserId);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("already linked");

        await _userEmailService.DidNotReceive()
            .AddVerifiedEmailAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // --- ApplyEmailBackfillAsync ---

    [HumansFact]
    public async Task ApplyEmailBackfillAsync_UpdatesEmailsAndReturnsCount()
    {
        var userId = Guid.NewGuid();
        _userService.ApplyEmailBackfillAsync(userId, "new@example.com", Arg.Any<CancellationToken>())
            .Returns((true, "old@example.com"));

        var corrections = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { userId.ToString(), "new@example.com" }
        };

        var result = await _service.ApplyEmailBackfillAsync(
            [userId], corrections, _actorUserId);

        result.UpdatedCount.Should().Be(1);
        result.Errors.Should().BeEmpty();
        await _userService.Received(1).ApplyEmailBackfillAsync(
            userId, "new@example.com", Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ApplyEmailBackfillAsync_SkipsUsersWithNoCorrection()
    {
        var userId = Guid.NewGuid();

        var result = await _service.ApplyEmailBackfillAsync(
            [userId], new Dictionary<string, string>(StringComparer.Ordinal), _actorUserId);

        result.UpdatedCount.Should().Be(0);
        await _userService.DidNotReceive().ApplyEmailBackfillAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ApplyEmailBackfillAsync_RecordsErrorWhenUserMissing()
    {
        var userId = Guid.NewGuid();
        _userService.ApplyEmailBackfillAsync(userId, "new@example.com", Arg.Any<CancellationToken>())
            .Returns((false, (string?)null));

        var corrections = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { userId.ToString(), "new@example.com" }
        };

        var result = await _service.ApplyEmailBackfillAsync(
            [userId], corrections, _actorUserId);

        result.UpdatedCount.Should().Be(0);
        result.Errors.Should().ContainSingle().Which.Should().Contain("not found");
    }

    // --- LinkGroupToTeamAsync ---

    [HumansFact]
    public async Task LinkGroupToTeamAsync_LinksGroupAndSaves()
    {
        var teamId = Guid.NewGuid();
        _teamService.SetGoogleGroupPrefixAsync(teamId, "test-team", Arg.Any<CancellationToken>())
            .Returns((true, (string?)null));
        _teamService.GetTeamByIdAsync(teamId, Arg.Any<CancellationToken>())
            .Returns(new Team { Id = teamId, Name = "Test Team", IsActive = true, Slug = "test-team" });
        _googleSyncService.EnsureTeamGroupAsync(teamId, false, Arg.Any<CancellationToken>())
            .Returns(GroupLinkResult.Ok());

        var result = await _service.LinkGroupToTeamAsync(teamId, "test-team");

        result.Success.Should().BeTrue();
        result.Message.Should().Contain("test-team@nobodies.team");

        await _teamService.Received(1).SetGoogleGroupPrefixAsync(
            teamId, "test-team", Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task LinkGroupToTeamAsync_ReturnsErrorIfTeamNotFound()
    {
        _teamService.SetGoogleGroupPrefixAsync(Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns((false, (string?)null));

        var result = await _service.LinkGroupToTeamAsync(Guid.NewGuid(), "prefix");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not found");
    }

    [HumansFact]
    public async Task LinkGroupToTeamAsync_RevertsOnError()
    {
        var teamId = Guid.NewGuid();
        _teamService.SetGoogleGroupPrefixAsync(teamId, "new-prefix", Arg.Any<CancellationToken>())
            .Returns((true, "old-prefix"));
        _teamService.GetTeamByIdAsync(teamId, Arg.Any<CancellationToken>())
            .Returns(new Team { Id = teamId, Name = "Test Team", IsActive = true, Slug = "test-team" });
        _googleSyncService.EnsureTeamGroupAsync(teamId, false, Arg.Any<CancellationToken>())
            .Returns(GroupLinkResult.Error("Failed to create group"));

        var result = await _service.LinkGroupToTeamAsync(teamId, "new-prefix");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Failed to create group");

        // Revert call — first SetGoogleGroupPrefixAsync with the new prefix, then with the previous.
        await _teamService.Received(1).SetGoogleGroupPrefixAsync(
            teamId, "old-prefix", Arg.Any<CancellationToken>());
    }

    // --- GetActiveTeamsAsync ---

    [HumansFact]
    public async Task GetActiveTeamsAsync_ReturnsOnlyActiveTeamsOrdered()
    {
        _teamService.GetActiveTeamOptionsAsync(Arg.Any<CancellationToken>())
            .Returns([
                new TeamOptionDto(Guid.NewGuid(), "Alpha"),
                new TeamOptionDto(Guid.NewGuid(), "Zebra")
            ]);

        var result = await _service.GetActiveTeamsAsync();

        result.Should().HaveCount(2);
        result[0].Name.Should().Be("Alpha");
        result[1].Name.Should().Be("Zebra");
    }
}
