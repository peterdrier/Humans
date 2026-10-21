using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Consent.Contracts;
using Humans.Base.Constants;
using Humans.Base.Enums;
using Humans.Email.Contracts;
using Humans.Governance.Contracts;
using Humans.Notifications.Contracts;
using Humans.Onboarding.Contracts;
using Humans.Onboarding.Services;
using Humans.Onboarding.Tests.Infrastructure;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;

namespace Humans.Onboarding.Tests.Services;

public sealed class OnboardingServiceTests
{
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IUserWrite _userServiceWrite = Substitute.For<IUserWrite>();
    private readonly IConsentWrite _consentWrite = Substitute.For<IConsentWrite>();
    private readonly Dictionary<Guid, ConsentReviewInfo> _reviews = [];
    private readonly IGovernanceService _applicationDecisionService = Substitute.For<IGovernanceService>();
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();
    private readonly INotificationEmitter _notificationService = Substitute.For<INotificationEmitter>();
    private readonly ISystemTeamSync _syncJob = Substitute.For<ISystemTeamSync>();
    private readonly IMembershipCalculator _membershipCalculator = Substitute.For<IMembershipCalculator>();
    private readonly IConsentService _consentService = Substitute.For<IConsentService>();
    private readonly IHumanLifecycleService _humanLifecycle = Substitute.For<IHumanLifecycleService>();
    private readonly IAuditLogService _auditLogService = Substitute.For<IAuditLogService>();
    private readonly OnboardingEmails _emailMessages = TestOnboardingEmails.Create();

    public OnboardingServiceTests()
    {
        _consentService.GetConsentReviewAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => _reviews.GetValueOrDefault(call.Arg<Guid>()) ?? ConsentReviewInfo.None(call.Arg<Guid>()));
        _consentService.GetConsentReviewsAsync(Arg.Any<CancellationToken>())
            .Returns(_ => _reviews);
        _userServiceWrite.SetRejectedAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new OnboardingResult(true));
    }

    private void Review(Guid userId, ConsentCheckStatus? status = null, bool isApproved = false, Instant? rejectedAt = null) =>
        _reviews[userId] = ConsentReviewInfo.None(userId) with
        {
            Status = status,
            IsApproved = isApproved,
            RejectedAt = rejectedAt,
        };

    private OnboardingService BuildSut() =>
        new(
            _userService,
            _userServiceWrite,
            _consentWrite,
            _applicationDecisionService,
            _emailService,
            _emailMessages,
            _notificationService,
            _syncJob,
            _membershipCalculator,
            _consentService,
            _humanLifecycle,
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

        _consentWrite.RecordConsentCheckAsync(
                userId, ConsentCheckStatus.Cleared, reviewerId, notes, Arg.Any<CancellationToken>())
            .Returns(new OnboardingResult(true));

        var result = await BuildSut().ClearConsentCheckAsync(userId, reviewerId, notes, Xunit.TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        await _auditLogService.Received(1).LogAsync(
            AuditAction.ConsentCheckCleared,
            AuditEntityTypes.Profile,
            userId,
            "Consent check cleared",
            reviewerId);
        await _syncJob.DidNotReceiveWithAnyArgs().SyncMembershipForUserAsync(default, default, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task RejectSignupAsync_WhenAlreadyRejectedInBothSections_SkipsAuditDeprovisionAndNotifications()
    {
        var userId = Guid.NewGuid();
        var reviewerId = Guid.NewGuid();

        _consentWrite.RecordRejectionAsync(userId, reviewerId, "duplicate", Arg.Any<CancellationToken>())
            .Returns(new OnboardingResult(false, "AlreadyRejected"));
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(
                UserInfoStubs.MakeUserInfo(userId, UserFixtures.Profile()) with { State = UserState.Rejected }));

        var result = await BuildSut().RejectSignupAsync(userId, reviewerId, "duplicate", Xunit.TestContext.Current.CancellationToken);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("AlreadyRejected");
        await _userServiceWrite.DidNotReceiveWithAnyArgs().SetRejectedAsync(default, Arg.Any<CancellationToken>());
        await _auditLogService.Received(0).LogAsync(
            AuditAction.SignupRejected,
            AuditEntityTypes.Profile,
            userId,
            Arg.Any<string>(),
            reviewerId);
        await _syncJob.DidNotReceiveWithAnyArgs().SyncMembershipForUserAsync(default, default, Arg.Any<CancellationToken>());
        await _emailService.DidNotReceive().SendAsync(
            Arg.Is<EmailMessage>(m => m.TemplateName == "signup_rejected"),
            Arg.Any<CancellationToken>());
        await _notificationService.DidNotReceiveWithAnyArgs().SendAsync(
            default,
            default,
            default,
            default!,
            default!, cancellationToken: Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task RejectSignupAsync_RetryAfterConsentOnlyWrite_ReconcilesStateAuditAndDeprovisionWithRecordedReason()
    {
        // The first attempt recorded Consent's rejection, then died before Users stored the state.
        // The retry gets AlreadyRejected from Consent and must still finish the job, once.
        var userId = Guid.NewGuid();
        var originalReviewer = Guid.NewGuid();
        var retryReviewer = Guid.NewGuid();

        _consentWrite.RecordRejectionAsync(userId, retryReviewer, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new OnboardingResult(false, "AlreadyRejected"));
        _reviews[userId] = ConsentReviewInfo.None(userId) with
        {
            RejectionReason = "duplicate account",
            RejectedAt = Instant.FromUtc(2026, 10, 1, 12, 0),
            RejectedByUserId = originalReviewer,
        };
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(
                UserInfoStubs.MakeUserInfo(userId, UserFixtures.Profile()) with { State = UserState.Active }));

        var result = await BuildSut()
            .RejectSignupAsync(userId, retryReviewer, "retry text", Xunit.TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        await _userServiceWrite.Received(1).SetRejectedAsync(userId, Arg.Any<CancellationToken>());
        await _auditLogService.Received(1).LogAsync(
            AuditAction.SignupRejected,
            AuditEntityTypes.Profile,
            userId,
            "Signup rejected: duplicate account",
            originalReviewer);
        await _syncJob.Received(3).SyncMembershipForUserAsync(
            userId, Arg.Any<SystemTeamType>(), Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task RejectSignupAsync_RecipientLookupFailure_DoesNotMisreportCommittedRejection(bool cancelled)
    {
        var userId = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();
        if (cancelled) await cancellation.CancelAsync();
        var failure = cancelled ? (Exception)new OperationCanceledException(cancellation.Token)
            : new IOException("Recipient lookup unavailable");
        _consentWrite.RecordRejectionAsync(userId, Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new OnboardingResult(true));
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromException<UserInfo?>(failure));
        OnboardingResult? result = null;

        var error = await Xunit.Record.ExceptionAsync(async () =>
            result = await BuildSut().RejectSignupAsync(userId, Guid.NewGuid(), null, cancellation.Token));

        if (cancelled)
            error.Should().BeSameAs(failure);
        else
        {
            error.Should().BeNull();
            result!.Success.Should().BeTrue();
            _notificationService.ReceivedCalls().Should().ContainSingle();
            var args = _notificationService.ReceivedCalls().Single().GetArguments();
            args[0].Should().Be(NotificationSource.ProfileRejected);
            args[3].Should().Be("Your signup has been reviewed");
            await _emailService.DidNotReceive().SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
        }
        _auditLogService.ReceivedCalls().Should().ContainSingle();
        _syncJob.ReceivedCalls().Should().HaveCount(3);
    }

    [HumansTheory]
    [Xunit.InlineData(null, "Tu inscripción no se ha podido aprobar en este momento.")]
    [Xunit.InlineData("Duplicate account", "Tu inscripción no se ha podido aprobar: Duplicate account")]
    public async Task RejectSignupAsync_LocalizesNotificationForRecipient(string? reason, string expectedBody)
    {
        var userId = Guid.NewGuid();
        _consentWrite.RecordRejectionAsync(userId, Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new OnboardingResult(true));
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(UserInfoStubs.MakeUserInfo(userId, UserFixtures.Profile(burnerName: "Member"))
                with
            { PreferredLanguage = "es" });

        (await BuildSut().RejectSignupAsync(userId, Guid.NewGuid(), reason,
            Xunit.TestContext.Current.CancellationToken)).Success.Should().BeTrue();

        var args = _notificationService.ReceivedCalls().Single().GetArguments();
        args[3].Should().Be("Se ha revisado tu inscripción");
        args[5].Should().Be(expectedBody);
        args[6].Should().Be("/Profile");
        args[7].Should().Be("Ver perfil");
    }

    [HumansTheory]
    [Xunit.InlineData("a")]
    [Xunit.InlineData("😀")]
    public async Task RejectSignupAsync_LongReason_BoundsNoticeAndPreservesStoredReason(string character)
    {
        var userId = Guid.NewGuid();
        var reviewerId = Guid.NewGuid();
        var reason = string.Concat(Enumerable.Repeat(character, 2000));
        _consentWrite.RecordRejectionAsync(userId, Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new OnboardingResult(true));
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(UserInfoStubs.MakeUserInfo(userId, UserFixtures.Profile(burnerName: "Member"))
                with
            { PreferredLanguage = "es" });

        var result = await BuildSut().RejectSignupAsync(userId, reviewerId, reason,
            Xunit.TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        var body = (string)_notificationService.ReceivedCalls().Single().GetArguments()[5]!;
        body.EnumerateRunes().Count().Should().Be(2000);
        body.Should().StartWith("Tu inscripción no se ha podido aprobar: ");
        body.Should().EndWith("…");
        body.Should().NotContain("�");
        await _consentWrite.Received(1).RecordRejectionAsync(userId, reviewerId, reason, Arg.Any<CancellationToken>());
        await _auditLogService.Received(1).LogAsync(
            AuditAction.SignupRejected, AuditEntityTypes.Profile, userId,
            "Signup rejected: " + reason, reviewerId);
        _syncJob.ReceivedCalls().Should().HaveCount(3);
    }

    [HumansFact]
    public async Task RejectSignupAsync_OnSuccess_AuditsDeprovisionsAllThreeTeamsAndNotifies()
    {
        // Reject is the section's only coordinator action with consequences (Docs/health.md §4),
        // and until this test its success path was unasserted: dropping one of the three
        // approval-gated team syncs, or the ProfileRejected notification, passed.
        var userId = Guid.NewGuid();
        var reviewerId = Guid.NewGuid();
        const string reason = "duplicate account";

        _consentWrite.RecordRejectionAsync(userId, reviewerId, reason, Arg.Any<CancellationToken>())
            .Returns(new OnboardingResult(true));
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(
                UserInfoStubs.MakeUserInfo(userId, UserFixtures.Profile(burnerName: "Rejected One"))));

        var result = await BuildSut()
            .RejectSignupAsync(userId, reviewerId, reason, Xunit.TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        await _userServiceWrite.Received(1).SetRejectedAsync(userId, Arg.Any<CancellationToken>());

        await _auditLogService.Received(1).LogAsync(
            AuditAction.SignupRejected,
            AuditEntityTypes.Profile,
            userId,
            $"Signup rejected: {reason}",
            reviewerId);

        // All three approval-gated system teams, and nothing else. Asserting each team
        // individually plus the total is what makes a dropped sync fail rather than pass.
        await _syncJob.Received(1).SyncMembershipForUserAsync(
            userId, SystemTeamType.Volunteers, Arg.Any<CancellationToken>());
        await _syncJob.Received(1).SyncMembershipForUserAsync(
            userId, SystemTeamType.Colaboradors, Arg.Any<CancellationToken>());
        await _syncJob.Received(1).SyncMembershipForUserAsync(
            userId, SystemTeamType.Asociados, Arg.Any<CancellationToken>());
        await _syncJob.Received(3).SyncMembershipForUserAsync(
            Arg.Any<Guid>(), Arg.Any<SystemTeamType>(), Arg.Any<CancellationToken>());

        await _emailService.Received(1).SendAsync(
            Arg.Is<EmailMessage>(m => m.TemplateName == "signup_rejected"
                && m.RecipientName == "Rejected One"
                && m.HtmlBody.Contains(reason)),
            Arg.Any<CancellationToken>());

        await _notificationService.Received(1).SendAsync(
            NotificationSource.ProfileRejected,
            NotificationClass.Informational,
            NotificationPriority.Normal,
            Arg.Any<string>(),
            Arg.Is<IReadOnlyList<Guid>>(ids => ids.Count == 1 && ids[0] == userId),
            body: Arg.Is<string?>(b => b != null && b.Contains(reason)),
            actionUrl: Arg.Any<string?>(),
            actionLabel: Arg.Any<string?>(),
            targetGroupName: Arg.Any<string?>(),
            sourceKey: Arg.Any<string?>(),
            cancellationToken: Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task SetConsentCheckPendingIfEligibleAsync_WhenEligible_UsesResolvedUserMutation(bool archivedId)
    {
        var userId = Guid.NewGuid();
        var requestedId = archivedId ? Guid.NewGuid() : userId;
        var profile = UserFixtures.Profile(
            burnerName: "Burner",
            firstName: "First",
            lastName: "Last",
            createdAt: Instant.FromUnixTimeSeconds(1));

        _userService.GetUserInfoAsync(requestedId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(UserInfoStubs.MakeUserInfo(userId, profile)));
        _membershipCalculator.HasAllRequiredConsentsForTeamAsync(
                userId,
                SystemTeamIds.Volunteers,
                Arg.Any<CancellationToken>())
            .Returns(true);
        _consentWrite.SetConsentCheckPendingAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new OnboardingResult(true));

        var set = await BuildSut().SetConsentCheckPendingIfEligibleAsync(requestedId, Xunit.TestContext.Current.CancellationToken);

        set.Should().BeTrue();
        await _consentWrite.Received(1).SetConsentCheckPendingAsync(userId, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetReviewQueueAsync_FlaggedUserAlreadyApproved_StillAppearsInFlagged()
    {
        // Invariant: anyone with an unresolved Flagged consent check must appear in the review
        // queue so a CC can resolve them — even if a prior admin override flipped IsApproved=true.
        var approvedFlaggedId = Guid.NewGuid();
        var now = Instant.FromUnixTimeSeconds(1);
        var approvedFlaggedProfile = UserFixtures.Profile(
            burnerName: "Burner",
            firstName: "Flagged",
            lastName: "Approved",
            createdAt: now);
        Review(approvedFlaggedId, ConsentCheckStatus.Flagged, isApproved: true);

        StubReviewQueueDependencies(
            [UserInfoStubs.MakeUserInfo(approvedFlaggedId, approvedFlaggedProfile)]);

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
        var rejectedFlaggedProfile = UserFixtures.Profile(
            burnerName: "Burner",
            firstName: "Flagged",
            lastName: "Rejected",
            createdAt: now);
        Review(rejectedFlaggedId, ConsentCheckStatus.Flagged, rejectedAt: now);

        StubReviewQueueDependencies(
            [UserInfoStubs.MakeUserInfo(rejectedFlaggedId, rejectedFlaggedProfile) with { State = UserState.Rejected }]);

        var data = await BuildSut().GetReviewQueueAsync(Xunit.TestContext.Current.CancellationToken);

        data.Flagged.Should().NotContain(u => u.Id == rejectedFlaggedId);
        data.Pending.Should().NotContain(u => u.Id == rejectedFlaggedId);
    }

    [HumansFact]
    public async Task GetReviewQueueAsync_MergedTombstoneNeedingReview_IsExcludedFromPending()
    {
        // Merge-source tombstones are not live accounts. Even though the merged row's profile
        // survives with names filled and IsApproved=false (which otherwise satisfies
        // NeedsConsentReview), it must never surface in the CC queue or its nav badge.
        var mergedId = Guid.NewGuid();
        var now = Instant.FromUnixTimeSeconds(1);
        var profile = UserFixtures.Profile(
            burnerName: "Burner",
            firstName: "Merged",
            lastName: "Away",
            createdAt: now);
        var mergedUser = new User
        {
            Id = mergedId,
            PreferredLanguage = "en",
            MergedAt = now,
            MergedToUserId = Guid.NewGuid(),
        };

        StubReviewQueueDependencies([mergedUser.ToUserInfo(profile: profile)]);

        var data = await BuildSut().GetReviewQueueAsync(Xunit.TestContext.Current.CancellationToken);

        data.Pending.Should().NotContain(u => u.Id == mergedId);
        data.Flagged.Should().NotContain(u => u.Id == mergedId);
    }

    [HumansFact]
    public async Task GetReviewQueueAsync_MergedTombstoneFlagged_IsExcludedFromFlagged()
    {
        // A merged tombstone whose surviving profile still carries a Flagged consent check must
        // not appear in the flagged queue — there is no live account left to clear.
        var mergedId = Guid.NewGuid();
        var now = Instant.FromUnixTimeSeconds(1);
        var profile = UserFixtures.Profile(
            burnerName: "Burner",
            firstName: "Flagged",
            lastName: "Merged",
            createdAt: now);
        Review(mergedId, ConsentCheckStatus.Flagged);
        var mergedUser = new User
        {
            Id = mergedId,
            PreferredLanguage = "en",
            MergedAt = now,
            MergedToUserId = Guid.NewGuid(),
        };

        StubReviewQueueDependencies([mergedUser.ToUserInfo(profile: profile)]);

        var data = await BuildSut().GetReviewQueueAsync(Xunit.TestContext.Current.CancellationToken);

        data.Flagged.Should().NotContain(u => u.Id == mergedId);
        data.Pending.Should().NotContain(u => u.Id == mergedId);
    }

    // --- GetNextUnsignedConsentAsync (widget consent step, G8/G9) ---

    [HumansFact]
    public async Task GetNextUnsignedConsent_AllSigned_SelfHealsSuspensionAndReturnsNullNext()
    {
        var userId = Guid.NewGuid();
        _consentService.GetRequiredConsentRowsForUserAsync(userId, SystemTeamIds.Volunteers, Arg.Any<CancellationToken>())
            .Returns([
                new RequiredConsentRow(Guid.NewGuid(), "Code of Conduct", Signed: true),
                new RequiredConsentRow(Guid.NewGuid(), "Privacy Policy", Signed: true),
            ]);

        var result = await BuildSut().GetNextUnsignedConsentAsync(userId, Xunit.TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(NextConsentStepOutcome.NothingLeftToSign);
        result.Next.Should().BeNull();
        await _humanLifecycle.Received(1).RestoreConsentSuspensionAsync(userId, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetNextUnsignedConsent_UnsignedRemaining_ReturnsDetailWithOrdinals_WithoutHealing()
    {
        var userId = Guid.NewGuid();
        var unsignedVersionId = Guid.NewGuid();
        _consentService.GetRequiredConsentRowsForUserAsync(userId, SystemTeamIds.Volunteers, Arg.Any<CancellationToken>())
            .Returns([
                new RequiredConsentRow(unsignedVersionId, "Code of Conduct", Signed: false),
                new RequiredConsentRow(Guid.NewGuid(), "Privacy Policy", Signed: true),
            ]);
        var detail = new ConsentReviewDetail(
            unsignedVersionId, "Code of Conduct", "2.0",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["en"] = "content" },
            Instant.FromUtc(2026, 1, 1, 0, 0), ChangesSummary: null,
            HasAlreadyConsented: false, ConsentedAt: null, UserFullName: null);
        _consentService.GetConsentReviewDetailAsync(unsignedVersionId, userId, Arg.Any<CancellationToken>())
            .Returns(detail);

        var result = await BuildSut().GetNextUnsignedConsentAsync(userId, Xunit.TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(NextConsentStepOutcome.Document);
        result.Next.Should().Be(detail);
        result.CurrentIndex.Should().Be(2); // 1 of 2 already signed → working on 2 of 2
        result.TotalRequired.Should().Be(2);
        await _humanLifecycle.DidNotReceiveWithAnyArgs().RestoreConsentSuspensionAsync(default, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetNextUnsignedConsent_DetailMissing_ReturnsNullNext_WithoutHealing()
    {
        var userId = Guid.NewGuid();
        var unsignedVersionId = Guid.NewGuid();
        _consentService.GetRequiredConsentRowsForUserAsync(userId, SystemTeamIds.Volunteers, Arg.Any<CancellationToken>())
            .Returns([new RequiredConsentRow(unsignedVersionId, "Code of Conduct", Signed: false)]);
        _consentService.GetConsentReviewDetailAsync(unsignedVersionId, userId, Arg.Any<CancellationToken>())
            .Returns((ConsentReviewDetail?)null);

        var result = await BuildSut().GetNextUnsignedConsentAsync(userId, Xunit.TestContext.Current.CancellationToken);

        // Distinct from NothingLeftToSign: the caller must not hand this back to the step
        // dispatcher, which would answer "consents" again and loop.
        result.Outcome.Should().Be(NextConsentStepOutcome.DocumentUnavailable);
        result.Next.Should().BeNull();
        await _humanLifecycle.DidNotReceiveWithAnyArgs().RestoreConsentSuspensionAsync(default, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task BulkClearConsentChecksAsync_ClearsOnlySelectedUsersWhoAreInTheQueue()
    {
        // Bulk clear's eligibility IS queue membership: a selected id that is not a queue row
        // must not be cleared. Without this, the only thing standing between a crafted POST and
        // clearing a rejected or merged profile is the queue-building code path.
        var now = Instant.FromUnixTimeSeconds(1);

        var flaggedId = Guid.NewGuid();
        var pendingId = Guid.NewGuid();
        var rejectedId = Guid.NewGuid();
        var unselectedId = Guid.NewGuid();
        var mergedId = Guid.NewGuid();

        var flagged = UserInfoStubs.MakeUserInfo(flaggedId, UserFixtures.Profile(
            burnerName: "Burner", firstName: "In", lastName: "Flagged",
            createdAt: now));
        Review(flaggedId, ConsentCheckStatus.Flagged);
        var pending = UserInfoStubs.MakeUserInfo(pendingId, UserFixtures.Profile(
            burnerName: "Burner", firstName: "In", lastName: "Pending",
            createdAt: now));
        var rejected = UserInfoStubs.MakeUserInfo(rejectedId, UserFixtures.Profile(
            burnerName: "Burner", firstName: "Out", lastName: "Rejected",
            createdAt: now)) with
        { State = UserState.Rejected };
        Review(rejectedId, ConsentCheckStatus.Flagged, rejectedAt: now);
        var unselected = UserInfoStubs.MakeUserInfo(unselectedId, UserFixtures.Profile(
            burnerName: "Burner", firstName: "Out", lastName: "Unselected",
            createdAt: now));
        var merged = new User
        {
            Id = mergedId,
            PreferredLanguage = "en",
            MergedAt = now,
            MergedToUserId = Guid.NewGuid(),
        }.ToUserInfo(profile: UserFixtures.Profile(
            burnerName: "Burner", firstName: "Out", lastName: "Merged",
            createdAt: now));
        Review(mergedId, ConsentCheckStatus.Flagged);

        StubReviewQueueDependencies([flagged, pending, rejected, unselected, merged]);
        _consentWrite.RecordConsentCheckAsync(
                Arg.Any<Guid>(), Arg.Any<ConsentCheckStatus>(), Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new OnboardingResult(true)));

        var reviewerId = Guid.NewGuid();
        var result = await BuildSut().BulkClearConsentChecksAsync(
            [flaggedId, pendingId, rejectedId, mergedId],
            reviewerId,
            Xunit.TestContext.Current.CancellationToken);

        result.ApprovedCount.Should().Be(2);

        foreach (var cleared in new[] { flaggedId, pendingId })
        {
            await _consentWrite.Received(1).RecordConsentCheckAsync(
                cleared, ConsentCheckStatus.Cleared, reviewerId, Arg.Any<string?>(), Arg.Any<CancellationToken>());
        }

        foreach (var untouched in new[] { rejectedId, unselectedId, mergedId })
        {
            await _consentWrite.DidNotReceive().RecordConsentCheckAsync(
                untouched, Arg.Any<ConsentCheckStatus>(), Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        }
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
