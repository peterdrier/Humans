using AwesomeAssertions;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Onboarding;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Onboarding;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;

namespace Humans.Application.Tests.Services.Onboarding;

public sealed class OnboardingServiceTests
{
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IApplicationServiceRead _applicationDecisionService = Substitute.For<IApplicationServiceRead>();
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();
    private readonly INotificationEmitter _notificationService = Substitute.For<INotificationEmitter>();
    private readonly ISystemTeamSync _syncJob = Substitute.For<ISystemTeamSync>();
    private readonly IMembershipCalculatorRead _membershipCalculator = Substitute.For<IMembershipCalculatorRead>();
    private readonly IAuditLogService _auditLogService = Substitute.For<IAuditLogService>();
    private readonly IEmailMessageFactory _emailMessages = Substitute.For<IEmailMessageFactory>();

    private OnboardingService BuildSut() =>
        new(
            _userService,
            _applicationDecisionService,
            _emailService,
            _emailMessages,
            _notificationService,
            _syncJob,
            _membershipCalculator,
            _auditLogService,
            NullLogger<OnboardingService>.Instance);

    [HumansFact]
    public async Task ClearConsentCheckAsync_RecordsClearedAnnotation_WithoutTeamSync()
    {
        // Name-only access switch: Clear is an audit annotation only. It records the Cleared
        // consent-check (+ audit) and must NOT provision any team — membership is reconciled by
        // SystemTeamSyncJob on name + consents, decoupled from CC review.
        var userId = Guid.NewGuid();
        var reviewerId = Guid.NewGuid();
        const string notes = "looks good";

        _userService.ApplyProfileOnboardingMutationAsync(
                userId,
                Arg.Is<UserProfileOnboardingCommand>(cmd =>
                    cmd.Mutation == UserProfileOnboardingMutation.RecordConsentCheck
                    && cmd.ActorUserId == reviewerId
                    && cmd.ConsentCheckStatus == ConsentCheckStatus.Cleared
                    && cmd.Notes == notes),
                Arg.Any<CancellationToken>())
            .Returns(new OnboardingResult(true));

        var result = await BuildSut().ClearConsentCheckAsync(userId, reviewerId, notes, Xunit.TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        await _auditLogService.Received(1).LogAsync(
            AuditAction.ConsentCheckCleared,
            nameof(Profile),
            userId,
            "Consent check cleared",
            reviewerId);
        await _syncJob.DidNotReceiveWithAnyArgs().SyncMembershipForUserAsync(default, default, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task RejectSignupAsync_WhenStorageFails_SkipsAuditDeprovisionAndNotifications()
    {
        var userId = Guid.NewGuid();
        var reviewerId = Guid.NewGuid();

        _userService.ApplyProfileOnboardingMutationAsync(
                userId,
                Arg.Is<UserProfileOnboardingCommand>(cmd =>
                    cmd.Mutation == UserProfileOnboardingMutation.RejectSignup
                    && cmd.ActorUserId == reviewerId
                    && cmd.RejectionReason == "duplicate"),
                Arg.Any<CancellationToken>())
            .Returns(new OnboardingResult(false, "AlreadyRejected"));

        var result = await BuildSut().RejectSignupAsync(userId, reviewerId, "duplicate", Xunit.TestContext.Current.CancellationToken);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("AlreadyRejected");
        await _auditLogService.Received(0).LogAsync(
            AuditAction.SignupRejected,
            nameof(Profile),
            userId,
            Arg.Any<string>(),
            reviewerId);
        await _syncJob.DidNotReceiveWithAnyArgs().SyncMembershipForUserAsync(default, default, Arg.Any<CancellationToken>());
        _emailMessages.DidNotReceiveWithAnyArgs().SignupRejected(default!, default!, default);
        await _notificationService.DidNotReceiveWithAnyArgs().SendAsync(
            default,
            default,
            default,
            default!,
            default!, cancellationToken: Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SetConsentCheckPendingIfEligibleAsync_WhenEligible_UsesUserServiceMutation()
    {
        var userId = Guid.NewGuid();
        var profile = new Profile
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            BurnerName = "Burner",
            FirstName = "First",
            LastName = "Last",
            State = ProfileState.Active,
            CreatedAt = Instant.FromUnixTimeSeconds(1),
            UpdatedAt = Instant.FromUnixTimeSeconds(1),
        };

        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(UserInfoStubHelpers.MakeUserInfo(userId, profile)));
        _membershipCalculator.HasAllRequiredConsentsForTeamAsync(
                userId,
                SystemTeamIds.Volunteers,
                Arg.Any<CancellationToken>())
            .Returns(true);
        _userService.ApplyProfileOnboardingMutationAsync(
                userId,
                Arg.Is<UserProfileOnboardingCommand>(cmd =>
                    cmd.Mutation == UserProfileOnboardingMutation.SetConsentCheckPending),
                Arg.Any<CancellationToken>())
            .Returns(new OnboardingResult(true));

        var set = await BuildSut().SetConsentCheckPendingIfEligibleAsync(userId, Xunit.TestContext.Current.CancellationToken);

        set.Should().BeTrue();
        await _userService.Received(1).ApplyProfileOnboardingMutationAsync(
            userId,
            Arg.Is<UserProfileOnboardingCommand>(cmd =>
                cmd.Mutation == UserProfileOnboardingMutation.SetConsentCheckPending),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetReviewQueueAsync_FlaggedUserAlreadyApproved_StillAppearsInFlagged()
    {
        // Invariant: anyone with an unresolved Flagged consent check must appear in the review
        // queue so a CC can resolve them — even if a prior admin override flipped IsApproved=true.
        var approvedFlaggedId = Guid.NewGuid();
        var now = Instant.FromUnixTimeSeconds(1);
        var approvedFlaggedProfile = new Profile
        {
            Id = Guid.NewGuid(),
            UserId = approvedFlaggedId,
            BurnerName = "Burner",
            FirstName = "Flagged",
            LastName = "Approved",
            State = ProfileState.Active,
            ConsentCheckStatus = ConsentCheckStatus.Flagged,
            IsApproved = true,
            CreatedAt = now,
            UpdatedAt = now,
        };

        StubReviewQueueDependencies(
            [UserInfoStubHelpers.MakeUserInfo(approvedFlaggedId, approvedFlaggedProfile)]);

        var data = await BuildSut().GetReviewQueueAsync(Xunit.TestContext.Current.CancellationToken);

        data.Flagged.Should().ContainSingle(u => u.Id == approvedFlaggedId);
        data.Pending.Should().NotContain(u => u.Id == approvedFlaggedId);
    }

    [HumansFact]
    public async Task GetReviewQueueAsync_FlaggedUserAlreadyRejected_IsExcluded()
    {
        // Rejected profiles have already been dealt with — Clear is blocked with AlreadyRejected,
        // so a flagged+rejected row would be unresolvable from the queue UI. Exclude them.
        var rejectedFlaggedId = Guid.NewGuid();
        var now = Instant.FromUnixTimeSeconds(1);
        var rejectedFlaggedProfile = new Profile
        {
            Id = Guid.NewGuid(),
            UserId = rejectedFlaggedId,
            BurnerName = "Burner",
            FirstName = "Flagged",
            LastName = "Rejected",
            State = ProfileState.Active,
            ConsentCheckStatus = ConsentCheckStatus.Flagged,
            RejectedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };

        StubReviewQueueDependencies(
            [UserInfoStubHelpers.MakeUserInfo(rejectedFlaggedId, rejectedFlaggedProfile)]);

        var data = await BuildSut().GetReviewQueueAsync(Xunit.TestContext.Current.CancellationToken);

        data.Flagged.Should().NotContain(u => u.Id == rejectedFlaggedId);
        data.Pending.Should().NotContain(u => u.Id == rejectedFlaggedId);
    }

    private void StubReviewQueueDependencies(IReadOnlyCollection<UserInfo> users)
    {
        _userService.GetAllUserInfosAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(users));
        _applicationDecisionService.GetUserIdsWithPendingApplicationAsync(
                Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>()));
        _membershipCalculator.GetMembershipSnapshotAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new MembershipSnapshot(
                Status: MembershipStatus.Pending,
                IsVolunteerMember: false,
                RequiredConsentCount: 0,
                PendingConsentCount: 0,
                MissingConsentVersionIds: []));
    }
}
