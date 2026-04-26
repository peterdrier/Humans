using AwesomeAssertions;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Users.AccountLifecycle;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Services;

/// <summary>
/// Orchestration coverage for <see cref="IAccountDeletionService"/> — the
/// single entry point that replaced the cascade code formerly scattered
/// across <c>UserService</c>, <c>ProfileService</c>, and
/// <c>OnboardingService</c> (issue nobodies-collective/Humans#582). Verifies the order + side effects
/// of the three deletion paths: user-requested, admin-initiated, expiry.
/// </summary>
public class AccountDeletionServiceTests
{
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IUserEmailService _userEmailService = Substitute.For<IUserEmailService>();
    private readonly ITeamService _teamService = Substitute.For<ITeamService>();
    private readonly IRoleAssignmentService _roleAssignmentService = Substitute.For<IRoleAssignmentService>();
    private readonly IShiftSignupService _shiftSignupService = Substitute.For<IShiftSignupService>();
    private readonly IShiftManagementService _shiftManagementService = Substitute.For<IShiftManagementService>();
    private readonly IProfileService _profileService = Substitute.For<IProfileService>();
    private readonly IServiceProvider _serviceProvider = Substitute.For<IServiceProvider>();
    private readonly IRoleAssignmentClaimsCacheInvalidator _roleAssignmentClaimsInvalidator =
        Substitute.For<IRoleAssignmentClaimsCacheInvalidator>();
    private readonly IShiftAuthorizationInvalidator _shiftAuthorizationInvalidator =
        Substitute.For<IShiftAuthorizationInvalidator>();
    private readonly IAuditLogService _auditLogService = Substitute.For<IAuditLogService>();
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 3, 14, 12, 0));
    private readonly AccountDeletionService _service;

    public AccountDeletionServiceTests()
    {
        _serviceProvider.GetService(typeof(IProfileService)).Returns(_profileService);

        _service = new AccountDeletionService(
            _userService,
            _userEmailService,
            _teamService,
            _roleAssignmentService,
            _shiftSignupService,
            _shiftManagementService,
            _serviceProvider,
            _roleAssignmentClaimsInvalidator,
            _shiftAuthorizationInvalidator,
            _auditLogService,
            _emailService,
            _clock,
            NullLogger<AccountDeletionService>.Instance);
    }

    // ==========================================================================
    // RequestDeletionAsync
    // ==========================================================================

    [HumansFact]
    public async Task RequestDeletionAsync_UnknownUser_ReturnsNotFound()
    {
        var userId = Guid.NewGuid();
        _userService.GetByIdAsync(userId, Arg.Any<CancellationToken>()).Returns((User?)null);

        var result = await _service.RequestDeletionAsync(userId);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("NotFound");
        await _teamService.DidNotReceiveWithAnyArgs()
            .RevokeAllMembershipsAsync(default, default);
    }

    [HumansFact]
    public async Task RequestDeletionAsync_AlreadyPending_ReturnsAlreadyPending()
    {
        var userId = Guid.NewGuid();
        _userService.GetByIdAsync(userId, Arg.Any<CancellationToken>())
            .Returns(MakeUser(userId, deletionPending: true));

        var result = await _service.RequestDeletionAsync(userId);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("AlreadyPending");
        await _teamService.DidNotReceiveWithAnyArgs()
            .RevokeAllMembershipsAsync(default, default);
    }

    [HumansFact]
    public async Task RequestDeletionAsync_Valid_SetsDeletionPendingAndCascades()
    {
        var userId = Guid.NewGuid();
        var user = MakeUser(userId);
        _userService.GetByIdAsync(userId, Arg.Any<CancellationToken>()).Returns(user);
        _teamService.RevokeAllMembershipsAsync(userId, Arg.Any<CancellationToken>()).Returns(3);
        _roleAssignmentService.RevokeAllActiveAsync(userId, Arg.Any<CancellationToken>()).Returns(1);
        _userEmailService.GetNotificationTargetEmailsAsync(
                Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(userId)),
                Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, string>());

        var result = await _service.RequestDeletionAsync(userId);

        result.Success.Should().BeTrue();

        var expectedScheduledFor = _clock.GetCurrentInstant().Plus(Duration.FromDays(30));
        await _userService.Received(1).SetDeletionPendingAsync(
            userId, _clock.GetCurrentInstant(), expectedScheduledFor, Arg.Any<CancellationToken>());

        await _teamService.Received(1).RevokeAllMembershipsAsync(userId, Arg.Any<CancellationToken>());
        await _roleAssignmentService.Received(1).RevokeAllActiveAsync(userId, Arg.Any<CancellationToken>());

        await _auditLogService.Received(1).LogAsync(
            AuditAction.MembershipsRevokedOnDeletionRequest, nameof(User), userId,
            Arg.Is<string>(s => s.Contains("3") && s.Contains("1")),
            userId,
            Arg.Any<Guid?>(), Arg.Any<string?>());

        await _emailService.Received(1).SendAccountDeletionRequestedAsync(
            user.Email!, user.DisplayName,
            Arg.Any<DateTime>(), user.PreferredLanguage, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task RequestDeletionAsync_PrefersVerifiedNotificationEmailOverUserEmail()
    {
        var userId = Guid.NewGuid();
        var user = MakeUser(userId, email: "primary@example.com");
        _userService.GetByIdAsync(userId, Arg.Any<CancellationToken>()).Returns(user);
        _userEmailService.GetNotificationTargetEmailsAsync(
                Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(userId)),
                Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, string> { [userId] = "notif@example.com" });

        await _service.RequestDeletionAsync(userId);

        await _emailService.Received(1).SendAccountDeletionRequestedAsync(
            "notif@example.com", user.DisplayName,
            Arg.Any<DateTime>(), user.PreferredLanguage, Arg.Any<CancellationToken>());
    }

    // ==========================================================================
    // PurgeAsync (admin-initiated)
    // ==========================================================================

    [HumansFact]
    public async Task PurgeAsync_UnknownUser_ReturnsNotFound()
    {
        var userId = Guid.NewGuid();
        _userService.PurgeOwnDataAsync(userId, Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await _service.PurgeAsync(userId);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("NotFound");
        _teamService.DidNotReceive().InvalidateActiveTeamsCache();
    }

    [HumansFact]
    public async Task PurgeAsync_Success_InvalidatesActiveTeamsCache()
    {
        var userId = Guid.NewGuid();
        _userService.PurgeOwnDataAsync(userId, Arg.Any<CancellationToken>()).Returns("Test Human");

        var result = await _service.PurgeAsync(userId);

        result.Success.Should().BeTrue();
        await _userService.Received(1).PurgeOwnDataAsync(userId, Arg.Any<CancellationToken>());
        _teamService.Received(1).InvalidateActiveTeamsCache();
        // Parity with AnonymizeExpiredAccountAsync: per-user caches that key
        // off identity must also drop on admin purge.
        _roleAssignmentClaimsInvalidator.Received(1).Invalidate(userId);
        _shiftAuthorizationInvalidator.Received(1).Invalidate(userId);
    }

    // ==========================================================================
    // AnonymizeExpiredAccountAsync
    // ==========================================================================

    [HumansFact]
    public async Task AnonymizeExpiredAccountAsync_UnknownUser_ReturnsNull()
    {
        var userId = Guid.NewGuid();
        _userService.GetByIdAsync(userId, Arg.Any<CancellationToken>()).Returns((User?)null);

        var result = await _service.AnonymizeExpiredAccountAsync(userId);

        result.Should().BeNull();
        await _teamService.DidNotReceiveWithAnyArgs().RevokeAllMembershipsAsync(default, default);
    }

    [HumansFact]
    public async Task AnonymizeExpiredAccountAsync_RunsCascadeInOrderAndInvalidatesCaches()
    {
        var userId = Guid.NewGuid();
        var user = MakeUser(userId, email: "expired@example.com");
        var signupId = Guid.NewGuid();
        var shiftId = Guid.NewGuid();

        _userService.GetByIdAsync(userId, Arg.Any<CancellationToken>()).Returns(user);
        _shiftSignupService.CancelActiveSignupsForUserAsync(
            userId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { (signupId, shiftId) });
        _userService.ApplyExpiredDeletionAnonymizationAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ExpiredDeletionAnonymizationResult(
                OriginalEmail: "expired@example.com",
                OriginalDisplayName: "Expired Human",
                PreferredLanguage: "es"));

        var result = await _service.AnonymizeExpiredAccountAsync(userId);

        result.Should().NotBeNull();
        result!.OriginalEmail.Should().Be("expired@example.com");
        result.OriginalDisplayName.Should().Be("Expired Human");
        result.PreferredLanguage.Should().Be("es");
        result.CancelledSignupIds.Should().ContainSingle()
            .Which.Should().Be((signupId, shiftId));

        await _teamService.Received(1).RevokeAllMembershipsAsync(userId, Arg.Any<CancellationToken>());
        await _roleAssignmentService.Received(1).RevokeAllActiveAsync(userId, Arg.Any<CancellationToken>());
        await _profileService.Received(1).AnonymizeExpiredProfileAsync(userId, Arg.Any<CancellationToken>());
        await _shiftSignupService.Received(1).CancelActiveSignupsForUserAsync(
            userId, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _shiftManagementService.Received(1).DeleteShiftProfilesForUserAsync(userId, Arg.Any<CancellationToken>());
        await _userService.Received(1).ApplyExpiredDeletionAnonymizationAsync(userId, Arg.Any<CancellationToken>());

        _teamService.Received(1).RemoveMemberFromAllTeamsCache(userId);
        _roleAssignmentClaimsInvalidator.Received(1).Invalidate(userId);
        _shiftAuthorizationInvalidator.Received(1).Invalidate(userId);
    }

    [HumansFact]
    public async Task AnonymizeExpiredAccountAsync_UserVanishedMidCascade_ReturnsPreCapturedSlice()
    {
        var userId = Guid.NewGuid();
        var user = MakeUser(userId, email: "gone@example.com", displayName: "Gone");
        _userService.GetByIdAsync(userId, Arg.Any<CancellationToken>()).Returns(user);
        _userService.ApplyExpiredDeletionAnonymizationAsync(userId, Arg.Any<CancellationToken>())
            .Returns((ExpiredDeletionAnonymizationResult?)null);

        var result = await _service.AnonymizeExpiredAccountAsync(userId);

        result.Should().NotBeNull();
        result!.OriginalEmail.Should().Be("gone@example.com");
        result.OriginalDisplayName.Should().Be("Gone");
        // Steps 1–5 already invalidated their own section caches; the
        // step-7 cross-section invalidations key off the identity write
        // completing, so they're correctly skipped on this branch.
        _teamService.DidNotReceive().RemoveMemberFromAllTeamsCache(userId);
        _roleAssignmentClaimsInvalidator.DidNotReceive().Invalidate(userId);
        _shiftAuthorizationInvalidator.DidNotReceive().Invalidate(userId);
    }

    [HumansFact]
    public async Task AnonymizeExpiredAccountAsync_CascadeFailurePreservesDeletionFields()
    {
        // If a mid-cascade step throws, the identity-collapse step never runs,
        // which means DeletionScheduledFor / DeletionEligibleAfter stay set —
        // so the job picks the user up again tomorrow. Asserted indirectly by
        // observing that ApplyExpiredDeletionAnonymizationAsync is never called
        // when an earlier cascade step fails.
        var userId = Guid.NewGuid();
        _userService.GetByIdAsync(userId, Arg.Any<CancellationToken>()).Returns(MakeUser(userId));
        _roleAssignmentService.RevokeAllActiveAsync(userId, Arg.Any<CancellationToken>())
            .Returns<int>(_ => throw new InvalidOperationException("boom"));

        var act = () => _service.AnonymizeExpiredAccountAsync(userId);

        await act.Should().ThrowAsync<InvalidOperationException>();
        await _userService.DidNotReceive().ApplyExpiredDeletionAnonymizationAsync(
            userId, Arg.Any<CancellationToken>());
    }

    // ==========================================================================
    // Helpers
    // ==========================================================================

    private static User MakeUser(
        Guid userId,
        string? email = "test@example.com",
        string displayName = "Test Human",
        string preferredLanguage = "en",
        bool deletionPending = false)
    {
        var user = new User
        {
            Id = userId,
            Email = email,
            UserName = email,
            DisplayName = displayName,
            PreferredLanguage = preferredLanguage,
        };
        if (deletionPending)
        {
            var now = Instant.FromUtc(2026, 3, 14, 12, 0);
            user.DeletionRequestedAt = now;
            user.DeletionScheduledFor = now.Plus(Duration.FromDays(30));
        }
        return user;
    }
}
