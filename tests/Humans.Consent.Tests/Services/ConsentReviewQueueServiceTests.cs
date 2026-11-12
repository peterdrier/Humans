using AwesomeAssertions;
using Humans.Consent.Contracts;
using Humans.Consent.Services;
using Humans.Governance.Contracts;
using Humans.Onboarding.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Consent.Tests.Services;

public sealed class ConsentReviewQueueServiceTests
{
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IConsentWrite _consentWrite = Substitute.For<IConsentWrite>();
    private readonly IConsentService _consentService = Substitute.For<IConsentService>();
    private readonly IGovernanceService _governance = Substitute.For<IGovernanceService>();
    private readonly Dictionary<Guid, ConsentReviewInfo> _reviews = [];

    public ConsentReviewQueueServiceTests()
    {
        _consentService.GetConsentReviewsAsync(Arg.Any<CancellationToken>())
            .Returns(_ => _reviews);
    }

    private void Review(Guid userId, ConsentCheckStatus? status = null, bool isApproved = false, Instant? rejectedAt = null) =>
        _reviews[userId] = ConsentReviewInfo.None(userId) with
        {
            Status = status,
            IsApproved = isApproved,
            RejectedAt = rejectedAt,
        };

    private ConsentReviewQueueService BuildSut() =>
        new(
            _userService,
            _consentService,
            _consentWrite,
            _governance,
            NullLogger<ConsentReviewQueueService>.Instance);

    [HumansFact]
    public async Task ClearAsync_RecordsClearedAnnotation()
    {
        // Name-only access switch: Clear is an audit annotation only. It records the Cleared consent-check, which
        // audits itself inside the write; membership is reconciled by SystemTeamSyncJob on name + consents.
        var userId = Guid.NewGuid();
        var reviewerId = Guid.NewGuid();
        const string notes = "looks good";

        _consentWrite.RecordConsentCheckAsync(
                userId, ConsentCheckStatus.Cleared, reviewerId, notes, Arg.Any<CancellationToken>())
            .Returns(new OnboardingResult(true));

        var result = await BuildSut().ClearAsync(userId, reviewerId, notes, TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        await _consentWrite.Received(1).RecordConsentCheckAsync(
            userId, ConsentCheckStatus.Cleared, reviewerId, notes, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetReviewQueueAsync_FlaggedUserAlreadyApproved_StillAppearsInFlagged()
    {
        // Invariant: anyone with an unresolved Flagged consent check must appear in the review
        // queue so a CC can resolve them — even if a prior admin override flipped IsApproved=true.
        var approvedFlaggedId = Guid.NewGuid();
        var now = Instant.FromUnixTimeSeconds(1);
        var approvedFlaggedProfile = UserFixtures.Profile(
            createdAt: now);
        Review(approvedFlaggedId, ConsentCheckStatus.Flagged, isApproved: true);

        StubReviewQueueDependencies(
            [MakeUserInfo(approvedFlaggedId, approvedFlaggedProfile, burnerName: "Burner", firstName: "Flagged", lastName: "Approved")]);

        var data = await BuildSut().GetReviewQueueAsync(TestContext.Current.CancellationToken);

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
            createdAt: now);
        Review(rejectedFlaggedId, ConsentCheckStatus.Flagged, rejectedAt: now);

        StubReviewQueueDependencies(
            [MakeUserInfo(rejectedFlaggedId, rejectedFlaggedProfile, burnerName: "Burner", firstName: "Flagged", lastName: "Rejected") with { State = UserState.Rejected }]);

        var data = await BuildSut().GetReviewQueueAsync(TestContext.Current.CancellationToken);

        data.Flagged.Should().NotContain(u => u.Id == rejectedFlaggedId);
        data.Pending.Should().NotContain(u => u.Id == rejectedFlaggedId);
    }

    [HumansTheory]
    [InlineData(null)]
    [InlineData(ConsentCheckStatus.Flagged)]
    public async Task GetReviewQueueAsync_FakeAccount_IsExcluded(ConsentCheckStatus? status)
    {
        // Gate terminal and dev personas: nobody to review, so neither list nor the nav badge shows them.
        var fakeId = Guid.NewGuid();
        var profile = UserFixtures.Profile(
            createdAt: Instant.FromUnixTimeSeconds(1));
        Review(fakeId, status);

        StubReviewQueueDependencies([MakeUserInfo(fakeId, profile, burnerName: "Gate Terminal", firstName: "Gate", lastName: "Terminal") with { IsFake = true }]);

        var data = await BuildSut().GetReviewQueueAsync(TestContext.Current.CancellationToken);

        data.Pending.Should().BeEmpty();
        data.Flagged.Should().BeEmpty();
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
            createdAt: now);
        var mergedUser = new User
        {
            Id = mergedId,
            PreferredLanguage = "en",
            MergedAt = now,
            MergedToUserId = Guid.NewGuid(),
        };

        StubReviewQueueDependencies([ToUserInfo(mergedUser, profile, burnerName: "Burner", firstName: "Merged", lastName: "Away")]);

        var data = await BuildSut().GetReviewQueueAsync(TestContext.Current.CancellationToken);

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
            createdAt: now);
        Review(mergedId, ConsentCheckStatus.Flagged);
        var mergedUser = new User
        {
            Id = mergedId,
            PreferredLanguage = "en",
            MergedAt = now,
            MergedToUserId = Guid.NewGuid(),
        };

        StubReviewQueueDependencies([ToUserInfo(mergedUser, profile, burnerName: "Burner", firstName: "Flagged", lastName: "Merged")]);

        var data = await BuildSut().GetReviewQueueAsync(TestContext.Current.CancellationToken);

        data.Flagged.Should().NotContain(u => u.Id == mergedId);
        data.Pending.Should().NotContain(u => u.Id == mergedId);
    }

    [HumansFact]
    public async Task BulkClearAsync_ClearsOnlySelectedUsersWhoAreInTheQueue()
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

        var flagged = MakeUserInfo(flaggedId, UserFixtures.Profile(
            createdAt: now), burnerName: "Burner", firstName: "In", lastName: "Flagged");
        Review(flaggedId, ConsentCheckStatus.Flagged);
        var pending = MakeUserInfo(pendingId, UserFixtures.Profile(
            createdAt: now), burnerName: "Burner", firstName: "In", lastName: "Pending");
        var rejected = MakeUserInfo(rejectedId, UserFixtures.Profile(
            createdAt: now), burnerName: "Burner", firstName: "Out", lastName: "Rejected") with
        { State = UserState.Rejected };
        Review(rejectedId, ConsentCheckStatus.Flagged, rejectedAt: now);
        var unselected = MakeUserInfo(unselectedId, UserFixtures.Profile(
            createdAt: now), burnerName: "Burner", firstName: "Out", lastName: "Unselected");
        var merged = ToUserInfo(
            new User
            {
                Id = mergedId,
                PreferredLanguage = "en",
                MergedAt = now,
                MergedToUserId = Guid.NewGuid(),
            },
            UserFixtures.Profile(createdAt: now), burnerName: "Burner", firstName: "Out", lastName: "Merged");
        Review(mergedId, ConsentCheckStatus.Flagged);

        StubReviewQueueDependencies([flagged, pending, rejected, unselected, merged]);
        _consentWrite.RecordConsentCheckAsync(
                Arg.Any<Guid>(), Arg.Any<ConsentCheckStatus>(), Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new OnboardingResult(true)));

        var reviewerId = Guid.NewGuid();
        var result = await BuildSut().BulkClearAsync(
            [flaggedId, pendingId, rejectedId, mergedId],
            reviewerId,
            TestContext.Current.CancellationToken);

        result.Should().Be(2);

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
        _governance.GetUserIdsWithPendingApplicationAsync(
                Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>()));
        _consentService.GetConsentProgressAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new ConsentProgress(0, []));
    }

    private static UserInfo ToUserInfo(User user, ProfileInfo profile, string burnerName = "", string firstName = "", string lastName = "")
    {
        user.BurnerName = burnerName;
        user.FirstName = firstName;
        user.LastName = lastName;
        return UserInfo.Create(user, [], [], profile: profile);
    }

    private static UserInfo MakeUserInfo(Guid userId, ProfileInfo profile, string burnerName = "", string firstName = "", string lastName = "") =>
        UserInfo.Create(
            new User { Id = userId, PreferredLanguage = "en", BurnerName = burnerName, FirstName = firstName, LastName = lastName, State = UserFixtures.StateFor(profile, burnerName, firstName, lastName) },
            [], [], profile: profile);
}
