using AwesomeAssertions;
using Humans.Application.Interfaces;
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
    private readonly IApplicationDecisionService _applicationDecisionService = Substitute.For<IApplicationDecisionService>();
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();
    private readonly INotificationService _notificationService = Substitute.For<INotificationService>();
    private readonly ISystemTeamSync _syncJob = Substitute.For<ISystemTeamSync>();
    private readonly IMembershipCalculator _membershipCalculator = Substitute.For<IMembershipCalculator>();
    private readonly IAuditLogService _auditLogService = Substitute.For<IAuditLogService>();
    private readonly IHumansMetrics _metrics = Substitute.For<IHumansMetrics>();

    private OnboardingService BuildSut() =>
        new(
            _userService,
            _applicationDecisionService,
            _emailService,
            _notificationService,
            _syncJob,
            _membershipCalculator,
            _auditLogService,
            _metrics,
            NullLogger<OnboardingService>.Instance);

    [HumansFact]
    public async Task ClearConsentCheckAsync_OnSuccess_UsesUserServiceMutationAndSyncsApprovedTeams()
    {
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
        _applicationDecisionService.GetApprovedTiersForUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns([MembershipTier.Colaborador, MembershipTier.Asociado]);

        var result = await BuildSut().ClearConsentCheckAsync(userId, reviewerId, notes);

        result.Success.Should().BeTrue();
        await _auditLogService.Received(1).LogAsync(
            AuditAction.ConsentCheckCleared,
            nameof(Profile),
            userId,
            "Consent check cleared",
            reviewerId);
        await _syncJob.Received(1).SyncMembershipForUserAsync(
            userId, SystemTeamType.Volunteers, Arg.Any<CancellationToken>());
        await _syncJob.Received(1).SyncMembershipForUserAsync(
            userId, SystemTeamType.Colaboradors, Arg.Any<CancellationToken>());
        await _syncJob.Received(1).SyncMembershipForUserAsync(
            userId, SystemTeamType.Asociados, Arg.Any<CancellationToken>());
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

        var result = await BuildSut().RejectSignupAsync(userId, reviewerId, "duplicate");

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("AlreadyRejected");
        await _auditLogService.Received(0).LogAsync(
            AuditAction.SignupRejected,
            nameof(Profile),
            userId,
            Arg.Any<string>(),
            reviewerId);
        await _syncJob.DidNotReceiveWithAnyArgs().SyncMembershipForUserAsync(default, default, default);
        await _emailService.DidNotReceiveWithAnyArgs().SendSignupRejectedAsync(default!, default!, default);
        await _notificationService.DidNotReceiveWithAnyArgs().SendAsync(
            default,
            default,
            default,
            default!,
            default!);
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

        var set = await BuildSut().SetConsentCheckPendingIfEligibleAsync(userId);

        set.Should().BeTrue();
        await _userService.Received(1).ApplyProfileOnboardingMutationAsync(
            userId,
            Arg.Is<UserProfileOnboardingCommand>(cmd =>
                cmd.Mutation == UserProfileOnboardingMutation.SetConsentCheckPending),
            Arg.Any<CancellationToken>());
    }
}
