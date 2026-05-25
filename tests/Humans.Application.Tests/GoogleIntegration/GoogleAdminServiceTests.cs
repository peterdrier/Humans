using AwesomeAssertions;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using GoogleAdminService = Humans.Application.Services.GoogleIntegration.GoogleAdminService;

namespace Humans.Application.Tests.GoogleIntegration;

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
        var teamResourceService = Substitute.For<ITeamResourceService>();
        _userService = Substitute.For<IUserService>();
        _userEmailService = Substitute.For<IUserEmailService>();
        _auditLogService = Substitute.For<IAuditLogService>();

        teamResourceService.GetActiveResourceCountsByTeamAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int>());

        // Default: Reset+2FA gate sees the account as not-yet-enrolled in 2SV,
        // so the recovery flow runs end-to-end. Tests that need the enrolled
        // path override this per-test.
        _workspaceUserService.GetAccountAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => new WorkspaceUserAccount(
                PrimaryEmail: ci.ArgAt<string>(0),
                FirstName: "Test",
                LastName: "User",
                IsSuspended: false,
                CreationTime: DateTime.UtcNow,
                LastLoginTime: null,
                IsEnrolledIn2Sv: false));

        _service = new GoogleAdminService(
            _workspaceUserService,
            _googleSyncService,
            _teamService,
            teamResourceService,
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
                    DateTime.UtcNow, DateTime.UtcNow, IsEnrolledIn2Sv: true,
                    RecoveryEmail: "alice.personal@example.com"),
                new WorkspaceUserAccount("bob@nobodies.team", "Bob", "Jones", true,
                    DateTime.UtcNow, null, IsEnrolledIn2Sv: false,
                    RecoveryEmail: null),
            ]);

        _userEmailService.MatchByEmailsAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns([
                new UserEmailMatch(
                    "alice@nobodies.team",
                    userId,
                    IsPrimary: true,
                    IsVerified: true,
                    UpdatedAt: SystemClock.Instance.GetCurrentInstant())
            ]);

        var testUser = new User { Id = userId, DisplayName = "Test User" };
        _userService.GetByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, User> { [userId] = testUser });
        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(
                new Dictionary<Guid, UserInfo> { [userId] = testUser.ToUserInfo() }));

        var result = await _service.GetWorkspaceAccountListAsync();

        result.TotalAccounts.Should().Be(2);
        result.ActiveAccounts.Should().Be(1);
        result.SuspendedAccounts.Should().Be(1);
        result.LinkedAccounts.Should().Be(1);
        result.UnlinkedAccounts.Should().Be(1);
        // Bob is unenrolled but suspended — suspended accounts are excluded
        // from the missing-2FA count by design (they can't sign in anyway).
        result.MissingTwoFactorCount.Should().Be(0);
        result.ErrorMessage.Should().BeNull();

        var alice = result.Accounts.Single(a =>
            string.Equals(a.PrimaryEmail, "alice@nobodies.team", StringComparison.OrdinalIgnoreCase));
        alice.MatchedUserId.Should().Be(userId);
        alice.IsUsedAsPrimary.Should().BeTrue();
        alice.RecoveryEmail.Should().Be("alice.personal@example.com");

        var bob = result.Accounts.Single(a =>
            string.Equals(a.PrimaryEmail, "bob@nobodies.team", StringComparison.OrdinalIgnoreCase));
        bob.RecoveryEmail.Should().BeNull();
    }

    [HumansFact]
    public async Task GetWorkspaceAccountListAsync_CountsActiveUnenrolledTowardMissingTwoFactor()
    {
        _workspaceUserService.ListAccountsAsync(Arg.Any<CancellationToken>())
            .Returns([
                new WorkspaceUserAccount("alice@nobodies.team", "Alice", "Smith", false,
                    DateTime.UtcNow, DateTime.UtcNow, IsEnrolledIn2Sv: true),
                new WorkspaceUserAccount("carol@nobodies.team", "Carol", "Doe", false,
                    DateTime.UtcNow, null, IsEnrolledIn2Sv: false),
                new WorkspaceUserAccount("bob@nobodies.team", "Bob", "Jones", true,
                    DateTime.UtcNow, null, IsEnrolledIn2Sv: false),
            ]);

        var result = await _service.GetWorkspaceAccountListAsync();

        result.MissingTwoFactorCount.Should().Be(1);
    }

    [HumansFact]
    public async Task GetWorkspaceAccountListAsync_PicksVerifiedWinner_WhenDuplicateEmailMatches()
    {
        // user_emails may hold both a verified and unverified row for the
        // same address. MatchByEmailsAsync surfaces both; the service must
        // collapse them rather than throw on the duplicate key.
        var verifiedUserId = Guid.NewGuid();
        var unverifiedUserId = Guid.NewGuid();
        var now = SystemClock.Instance.GetCurrentInstant();

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
                    IsPrimary: false,
                    IsVerified: false,
                    UpdatedAt: now),
                new UserEmailMatch(
                    "dup@nobodies.team", verifiedUserId,
                    IsPrimary: true,
                    IsVerified: true,
                    UpdatedAt: now - Duration.FromHours(1)),
            ]);

        var verifiedUser = new User { Id = verifiedUserId, DisplayName = "Verified User" };
        _userService.GetByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, User> { [verifiedUserId] = verifiedUser });
        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(
                new Dictionary<Guid, UserInfo> { [verifiedUserId] = verifiedUser.ToUserInfo() }));

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
    public async Task ProvisionStandaloneAccountAsync_ReturnsErrorForMissingRequiredFields()
    {
        var result = await _service.ProvisionStandaloneAccountAsync(
            "test",
            "",
            "User",
            _actorUserId);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("All fields are required.");

        await _workspaceUserService.DidNotReceiveWithAnyArgs().GetAccountAsync(null!, CancellationToken.None);
        await _workspaceUserService.DidNotReceiveWithAnyArgs().ProvisionAccountAsync(null!, null!, null!, null!, null, CancellationToken.None);
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
            Arg.Any<AuditAction>(),
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
        var commsTeamId = Guid.NewGuid();
        var commsTeam = new TeamInfo(
            commsTeamId, "Communications", null, "communications",
            IsActive: true, IsSystemTeam: false, SystemTeamType: SystemTeamType.None,
            RequiresApproval: false, IsPublicPage: false, IsHidden: false,
            IsPromotedToDirectory: false, CreatedAt: Instant.MinValue,
            Members: [],
            GoogleGroupPrefix: "comms");
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, TeamInfo> { [commsTeamId] = commsTeam });

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

    [HumansFact]
    public async Task ResetPasswordAsync_ReturnsErrorForMissingEmail()
    {
        var result = await _service.ResetPasswordAsync("", _actorUserId);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("Email is required.");

        await _workspaceUserService.DidNotReceiveWithAnyArgs().ResetPasswordAsync(null!, null!, CancellationToken.None);
    }

    [HumansFact]
    public async Task ResetPasswordAsync_ReturnsPasswordEvenWhenAuditWriteFails()
    {
        // Workspace has already rotated the password — if the audit-side
        // lookup or LogAsync throws (DB outage, timeout), the caller still
        // needs the new temporary password back. Otherwise they retry the
        // reset, lock the human out further, and we own the confusion.
        _auditLogService.LogAsync(
                AuditAction.WorkspaceAccountPasswordReset,
                Arg.Any<string>(), Arg.Any<Guid>(),
                Arg.Any<string>(), Arg.Any<Guid>(),
                Arg.Any<Guid?>(), Arg.Any<string?>())
            .ThrowsAsync(new InvalidOperationException("Audit DB unavailable"));

        var result = await _service.ResetPasswordAsync(
            "alice@nobodies.team", _actorUserId);

        result.Success.Should().BeTrue();
        result.TemporaryPassword.Should().NotBeNullOrEmpty();
        result.Message.Should().Contain("Password reset");
    }

    [HumansFact]
    public async Task ResetPasswordAsync_ReturnsPasswordEvenWhenLinkedUserLookupFails()
    {
        // Same as above for the email-match leg: a DB outage in
        // MatchByEmailsAsync must not convert a successful Workspace reset
        // into a failure result.
        _userEmailService.MatchByEmailsAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("user_emails query failed"));

        var result = await _service.ResetPasswordAsync(
            "alice@nobodies.team", _actorUserId);

        result.Success.Should().BeTrue();
        result.TemporaryPassword.Should().NotBeNullOrEmpty();
    }

    [HumansFact]
    public async Task ResetPasswordAsync_AuditsLinkedHumanAsSubject()
    {
        // When the workspace address resolves to a human, the audit row's
        // EntityId must carry that UserId so the activity feed renders the
        // human's name as subject instead of "Unknown".
        var linkedUserId = Guid.NewGuid();
        _userEmailService.MatchByEmailsAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns([
                new UserEmailMatch(
                    "ben.tree@nobodies.team", linkedUserId,
                    IsPrimary: false, IsVerified: true,
                    UpdatedAt: SystemClock.Instance.GetCurrentInstant())
            ]);

        await _service.ResetPasswordAsync("ben.tree@nobodies.team", _actorUserId);

        await _auditLogService.Received(1).LogAsync(
            AuditAction.WorkspaceAccountPasswordReset,
            "WorkspaceAccount", linkedUserId,
            Arg.Is<string>(s => s.Contains("ben.tree@nobodies.team")),
            _actorUserId,
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    // --- GenerateBackupCodesAsync (tested via ResetPasswordAndGenerate2FaAsync) ---

    [HumansFact]
    public async Task GenerateBackupCodesAsync_ReturnsCodesAndAuditsOnSuccess()
    {
        // GenerateBackupCodesAsync is private; exercise it through the public combined method.
        IReadOnlyList<string> issued = ["aaaa-1111", "bbbb-2222", "cccc-3333"];
        _workspaceUserService.GenerateBackupCodesAsync(
                "alice@nobodies.team", Arg.Any<CancellationToken>())
            .Returns(issued);

        var result = await _service.ResetPasswordAndGenerate2FaAsync(
            "alice@nobodies.team", _actorUserId);

        result.Success.Should().BeTrue();
        result.Email.Should().Be("alice@nobodies.team");
        // The combined method exposes the first code as BackupCode.
        result.BackupCode.Should().Be("aaaa-1111");

        await _auditLogService.Received(1).LogAsync(
            AuditAction.WorkspaceAccountBackupCodesGenerated,
            "WorkspaceAccount", Guid.Empty,
            Arg.Is<string>(s => s.Contains("alice@nobodies.team") && s.Contains("3 code")),
            _actorUserId,
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task GenerateBackupCodesAsync_ReturnsFailureAndDoesNotAuditOnEmptyList()
    {
        // Generate succeeded on Google's side but List returned 0 — we
        // must not write a misleading "generated 0 codes" audit entry.
        _workspaceUserService.GenerateBackupCodesAsync(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var result = await _service.ResetPasswordAndGenerate2FaAsync(
            "alice@nobodies.team", _actorUserId);

        // Combined method returns Success=true with password-only on backup-code failure.
        result.Success.Should().BeTrue();
        result.BackupCode.Should().BeNull();
        result.Message.Should().Contain("none were returned");

        await _auditLogService.DidNotReceive().LogAsync(
            AuditAction.WorkspaceAccountBackupCodesGenerated,
            Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task GenerateBackupCodesAsync_ReturnsErrorOnWorkspaceFailure()
    {
        _workspaceUserService.GenerateBackupCodesAsync(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Google API error"));

        var result = await _service.ResetPasswordAndGenerate2FaAsync(
            "alice@nobodies.team", _actorUserId);

        // Combined method surfaces backup-code failure as Success=true, BackupCode=null.
        result.Success.Should().BeTrue();
        result.BackupCode.Should().BeNull();
        result.Message.Should().Contain("Backup-code generation failed");

        await _auditLogService.DidNotReceive().LogAsync(
            AuditAction.WorkspaceAccountBackupCodesGenerated,
            Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task GenerateBackupCodesAsync_ReturnsCodesEvenWhenAuditWriteFails()
    {
        // Google has already invalidated the previously-issued set, so dropping
        // the new codes locks the human out. The audit failure for backup-code
        // generation is logged loudly but the code must still flow back to the admin.
        IReadOnlyList<string> issued = ["aaaa-1111", "bbbb-2222"];
        _workspaceUserService.GenerateBackupCodesAsync(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(issued);
        // Only fail the backup-codes audit; let the password-reset audit succeed
        // so the combined method reaches the backup-code path.
        _auditLogService.LogAsync(
                AuditAction.WorkspaceAccountBackupCodesGenerated,
                Arg.Any<string>(), Arg.Any<Guid>(),
                Arg.Any<string>(), Arg.Any<Guid>(),
                Arg.Any<Guid?>(), Arg.Any<string?>())
            .ThrowsAsync(new InvalidOperationException("Audit DB unavailable"));

        var result = await _service.ResetPasswordAndGenerate2FaAsync(
            "alice@nobodies.team", _actorUserId);

        result.Success.Should().BeTrue();
        result.BackupCode.Should().Be("aaaa-1111");
    }

    // --- ResetPasswordAndGenerate2FaAsync ---

    [HumansFact]
    public async Task ResetPasswordAndGenerate2FaAsync_ReturnsBothCredentialsOnSuccess()
    {
        IReadOnlyList<string> issued = ["aaaa-1111", "bbbb-2222", "cccc-3333"];
        _workspaceUserService.GenerateBackupCodesAsync(
                "alice@nobodies.team", Arg.Any<CancellationToken>())
            .Returns(issued);

        var result = await _service.ResetPasswordAndGenerate2FaAsync(
            "alice@nobodies.team", _actorUserId);

        result.Success.Should().BeTrue();
        result.Email.Should().Be("alice@nobodies.team");
        result.TempPassword.Should().NotBeNullOrEmpty();
        // The recovery flow uses one code, not the full set.
        result.BackupCode.Should().Be("aaaa-1111");

        await _workspaceUserService.Received(1).ResetPasswordAsync(
            "alice@nobodies.team", Arg.Any<string>(), Arg.Any<CancellationToken>());

        // Two audit entries — one for the password reset, one for the codes generation.
        await _auditLogService.Received(1).LogAsync(
            AuditAction.WorkspaceAccountPasswordReset,
            "WorkspaceAccount", Guid.Empty,
            Arg.Any<string>(), _actorUserId,
            Arg.Any<Guid?>(), Arg.Any<string?>());
        await _auditLogService.Received(1).LogAsync(
            AuditAction.WorkspaceAccountBackupCodesGenerated,
            "WorkspaceAccount", Guid.Empty,
            Arg.Any<string>(), _actorUserId,
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task ResetPasswordAndGenerate2FaAsync_ReturnsFailureWhenPasswordResetFails()
    {
        _workspaceUserService.ResetPasswordAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Google API error"));

        var result = await _service.ResetPasswordAndGenerate2FaAsync(
            "alice@nobodies.team", _actorUserId);

        result.Success.Should().BeFalse();
        result.TempPassword.Should().BeNull();
        result.BackupCode.Should().BeNull();
        result.ErrorMessage.Should().Contain("Failed to reset password");

        // Backup-code generation must NOT be attempted when password reset failed.
        await _workspaceUserService.DidNotReceive().GenerateBackupCodesAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ResetPasswordAndGenerate2FaAsync_ReturnsPasswordOnlyWhenBackupCodesFail()
    {
        // Password reset succeeds, but Google rejects the backup-code request.
        // The admin still gets the password — partial success is more useful
        // than asking them to retry from scratch (Google has already rotated).
        _workspaceUserService.GenerateBackupCodesAsync(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Google API error"));

        var result = await _service.ResetPasswordAndGenerate2FaAsync(
            "alice@nobodies.team", _actorUserId);

        result.Success.Should().BeTrue();
        result.TempPassword.Should().NotBeNullOrEmpty();
        result.BackupCode.Should().BeNull();
        result.Message.Should().Contain("Backup-code generation failed");
    }

    [HumansFact]
    public async Task ResetPasswordAndGenerate2FaAsync_RefusesAndAuditsWhenAlreadyEnrolledIn2Sv()
    {
        // Live Directory state says the human already has 2-Step Verification.
        // Running the combined flow would destructively rotate their working
        // 2FA setup, so the service must refuse — even if the UI button was
        // somehow bypassed by a hand-crafted POST.
        _workspaceUserService.GetAccountAsync(
                "alice@nobodies.team", Arg.Any<CancellationToken>())
            .Returns(new WorkspaceUserAccount(
                "alice@nobodies.team", "Alice", "Smith", IsSuspended: false,
                CreationTime: DateTime.UtcNow, LastLoginTime: null,
                IsEnrolledIn2Sv: true));

        var result = await _service.ResetPasswordAndGenerate2FaAsync(
            "alice@nobodies.team", _actorUserId);

        result.Success.Should().BeFalse();
        result.TempPassword.Should().BeNull();
        result.BackupCode.Should().BeNull();
        result.ErrorMessage.Should().Contain("already enrolled in 2FA");

        // No password reset, no backup-code rotation.
        await _workspaceUserService.DidNotReceive().ResetPasswordAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _workspaceUserService.DidNotReceive().GenerateBackupCodesAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());

        // Refusal is audited so the attempt shows up in the trail.
        await _auditLogService.Received(1).LogAsync(
            AuditAction.WorkspaceAccountResetBlockedFor2Sv,
            "WorkspaceAccount", Guid.Empty,
            Arg.Is<string>(s => s.Contains("alice@nobodies.team")),
            _actorUserId,
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task ResetPasswordAndGenerate2FaAsync_StillRefusesWhenAuditWriteFailsOnEnrolledAccount()
    {
        // The 2SV refusal happens BEFORE any Workspace mutation, so an audit
        // outage must not turn a safe refusal into a 500 — we surface the
        // refusal regardless and log the audit failure as Critical.
        _workspaceUserService.GetAccountAsync(
                "alice@nobodies.team", Arg.Any<CancellationToken>())
            .Returns(new WorkspaceUserAccount(
                "alice@nobodies.team", "Alice", "Smith", IsSuspended: false,
                CreationTime: DateTime.UtcNow, LastLoginTime: null,
                IsEnrolledIn2Sv: true));
        _auditLogService.LogAsync(
                AuditAction.WorkspaceAccountResetBlockedFor2Sv,
                Arg.Any<string>(), Arg.Any<Guid>(),
                Arg.Any<string>(), Arg.Any<Guid>(),
                Arg.Any<Guid?>(), Arg.Any<string?>())
            .ThrowsAsync(new InvalidOperationException("Audit DB unavailable"));

        var result = await _service.ResetPasswordAndGenerate2FaAsync(
            "alice@nobodies.team", _actorUserId);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("already enrolled in 2FA");

        await _workspaceUserService.DidNotReceive().ResetPasswordAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _workspaceUserService.DidNotReceive().GenerateBackupCodesAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ResetPasswordAndGenerate2FaAsync_ReturnsErrorWhenAccountNotFound()
    {
        _workspaceUserService.GetAccountAsync(
                "ghost@nobodies.team", Arg.Any<CancellationToken>())
            .Returns((WorkspaceUserAccount?)null);

        var result = await _service.ResetPasswordAndGenerate2FaAsync(
            "ghost@nobodies.team", _actorUserId);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not found");

        await _workspaceUserService.DidNotReceive().ResetPasswordAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ResetPasswordAndGenerate2FaAsync_ReturnsErrorForMissingEmail()
    {
        var result = await _service.ResetPasswordAndGenerate2FaAsync("", _actorUserId);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("Email is required.");

        await _workspaceUserService.DidNotReceiveWithAnyArgs().GetAccountAsync(null!, CancellationToken.None);
        await _workspaceUserService.DidNotReceiveWithAnyArgs().ResetPasswordAsync(null!, null!, CancellationToken.None);
    }

    // --- LinkAccountAsync ---

    [HumansFact]
    public async Task LinkAccountAsync_LinksEmailAndStampsIsGoogle()
    {
        var userId = Guid.NewGuid();
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new User { Id = userId, DisplayName = "Test User" }.ToUserInfo());
        _userEmailService.IsEmailLinkedToAnyUserAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _service.LinkAccountAsync(
            "alice@nobodies.team", userId, _actorUserId);

        result.Success.Should().BeTrue();
        result.Message.Should().Contain("Linked");

        // Issue nobodies-collective/Humans#687: AddVerifiedEmailAsync creates the
        // row and the UserEmailService orchestrator stamps IsGoogle via
        // EnsureGoogleInvariantAsync — no separate SetGoogleEmailAsync call to
        // User.GoogleEmail. GoogleEmailStatus is reset explicitly so
        // reconciliation resumes.
        await _userEmailService.Received(1)
            .AddVerifiedEmailAsync(userId, "alice@nobodies.team", Arg.Any<CancellationToken>());
        await _userService.Received(1)
            .TrySetGoogleEmailStatusFromSyncAsync(
                userId, GoogleEmailStatus.Unknown, Arg.Any<CancellationToken>());
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
        _userService.GetUserInfoAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((UserInfo?)null);

        var result = await _service.LinkAccountAsync(
            "alice@nobodies.team", Guid.NewGuid(), _actorUserId);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not found");
    }

    [HumansFact]
    public async Task LinkAccountAsync_ReturnsErrorIfEmailConflict()
    {
        var userId = Guid.NewGuid();
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new User { Id = userId, DisplayName = "Test" }.ToUserInfo());
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
        var alphaId = Guid.NewGuid();
        var zebraId = Guid.NewGuid();
        var teams = new Dictionary<Guid, TeamInfo>
        {
            [alphaId] = new(
                alphaId, "Alpha", null, "alpha",
                IsActive: true, IsSystemTeam: false, SystemTeamType: SystemTeamType.None,
                RequiresApproval: false, IsPublicPage: false, IsHidden: false,
                IsPromotedToDirectory: false, CreatedAt: Instant.MinValue,
                Members: []),
            [zebraId] = new(
                zebraId, "Zebra", null, "zebra",
                IsActive: true, IsSystemTeam: false, SystemTeamType: SystemTeamType.None,
                RequiresApproval: false, IsPublicPage: false, IsHidden: false,
                IsPromotedToDirectory: false, CreatedAt: Instant.MinValue,
                Members: []),
        };
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(teams);

        var result = await _service.GetActiveTeamsAsync();

        result.Should().HaveCount(2);
        result[0].Name.Should().Be("Alpha");
        result[1].Name.Should().Be("Zebra");
    }
}
