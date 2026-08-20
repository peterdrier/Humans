using Humans.Governance.Tests.Infrastructure;
using AwesomeAssertions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Humans.Governance.Services;
using Humans.Base.Constants;
using Humans.Consent.Contracts;
using Humans.Users.Contracts;

namespace Humans.Governance.Tests.Services;

public class MembershipPartitionTests
{
    private readonly FakeClock _clock;
    private readonly MembershipCalculator _service;
    private readonly IMembershipQuery _membershipQuery = Substitute.For<IMembershipQuery>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IConsentServiceRead _consentService = Substitute.For<IConsentServiceRead>();
    private readonly ILegalDocumentSyncServiceRead _legalDocumentSyncService = Substitute.For<ILegalDocumentSyncServiceRead>();

    private readonly Dictionary<Guid, User> _usersById = new();
    private readonly Dictionary<Guid, ProfileInfo> _profilesByUserId = new();
    private readonly Dictionary<Guid, List<RequiredDocumentVersionSnapshot>> _requiredVersionsByTeam = new();
    private readonly Dictionary<Guid, HashSet<Guid>> _consentedVersionsByUser = new();

    public MembershipPartitionTests()
    {
        _clock = new FakeClock(Instant.FromUtc(2026, 3, 1, 12, 0));

        var serviceProvider = new ServiceLocatorBuilder()
            .With(_consentService)
            .Build();

        _service = new MembershipCalculator(
            _membershipQuery,
            _userService,
            _legalDocumentSyncService,
            serviceProvider,
            _clock);

        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var ids = ci.Arg<IReadOnlyCollection<Guid>>();
                IReadOnlyDictionary<Guid, UserInfo> map = ids
                    .Where(_usersById.ContainsKey)
                    .ToDictionary(
                        id => id,
                        id => _usersById[id].ToUserInfo(
                            profile: _profilesByUserId.GetValueOrDefault(id)));
                return new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(map);
            });

        _legalDocumentSyncService.GetRequiredDocumentVersionsForTeamAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var teamId = ci.Arg<Guid>();
                var versions = _requiredVersionsByTeam.GetValueOrDefault(teamId) ?? [];
                return Task.FromResult<IReadOnlyList<RequiredDocumentVersionSnapshot>>(versions);
            });

        _consentService.GetConsentMapForUsersAsync(
            Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var userIds = ci.Arg<IReadOnlyList<Guid>>();
                var result = userIds.ToDictionary(
                    id => id,
                    id => (IReadOnlySet<Guid>)(_consentedVersionsByUser.GetValueOrDefault(id) ?? []));
                return Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlySet<Guid>>>(result);
            });
    }


    [HumansFact]
    public async Task PartitionUsersAsync_ActiveUser_GoesToActiveBucket()
    {
        var userId = SeedUser();
        SeedProfile(userId, isApproved: true, isSuspended: false);
        var versionId = SeedRequiredVersion(SystemTeamIds.Volunteers);
        SeedConsent(userId, versionId);

        var result = await _service.PartitionUsersAsync([userId], Xunit.TestContext.Current.CancellationToken);

        result.Active.Should().Contain(userId);
        result.PendingApproval.Should().NotContain(userId);
        result.Suspended.Should().NotContain(userId);
        result.IncompleteSignup.Should().NotContain(userId);
        result.PendingDeletion.Should().NotContain(userId);
        result.MissingConsents.Should().NotContain(userId);
    }

    [HumansFact]
    public async Task PartitionUsersAsync_PendingApproval_GoesToPendingApprovalBucket()
    {
        var userId = SeedUser();
        SeedProfile(userId, isApproved: false, isSuspended: false);

        var result = await _service.PartitionUsersAsync([userId], Xunit.TestContext.Current.CancellationToken);

        result.PendingApproval.Should().Contain(userId);
        result.Active.Should().NotContain(userId);
    }

    [HumansFact]
    public async Task PartitionUsersAsync_Suspended_GoesToSuspendedBucket()
    {
        var userId = SeedUser();
        SeedProfile(userId, isApproved: true, isSuspended: true);

        var result = await _service.PartitionUsersAsync([userId], Xunit.TestContext.Current.CancellationToken);

        result.Suspended.Should().Contain(userId);
        result.Active.Should().NotContain(userId);
    }

    [HumansFact]
    public async Task PartitionUsersAsync_IncompleteSignup_GoesToIncompleteSignupBucket()
    {
        var userId = SeedUser();
        // No profile seeded

        var result = await _service.PartitionUsersAsync([userId], Xunit.TestContext.Current.CancellationToken);

        result.IncompleteSignup.Should().Contain(userId);
        result.Active.Should().NotContain(userId);
    }

    [HumansFact]
    public async Task PartitionUsersAsync_PendingDeletion_GoesToPendingDeletionBucket()
    {
        var userId = SeedUser(deletionRequestedAt: _clock.GetCurrentInstant());
        SeedProfile(userId, isApproved: true, isSuspended: false);

        var result = await _service.PartitionUsersAsync([userId], Xunit.TestContext.Current.CancellationToken);

        result.PendingDeletion.Should().Contain(userId);
        result.Active.Should().NotContain(userId);
    }

    [HumansFact]
    public async Task PartitionUsersAsync_MissingConsents_GoesToMissingConsentsBucket()
    {
        var userId = SeedUser();
        SeedProfile(userId, isApproved: true, isSuspended: false);
        SeedRequiredVersion(SystemTeamIds.Volunteers); // required doc, no consent record

        var result = await _service.PartitionUsersAsync([userId], Xunit.TestContext.Current.CancellationToken);

        result.MissingConsents.Should().Contain(userId);
        result.Active.Should().NotContain(userId);
    }

    [HumansFact]
    public async Task PartitionUsersAsync_AllBucketsSumToTotal()
    {
        // Seed one user per category
        var activeUser = SeedUser();
        SeedProfile(activeUser, isApproved: true, isSuspended: false);
        var versionId = SeedRequiredVersion(SystemTeamIds.Volunteers);
        SeedConsent(activeUser, versionId);

        var pendingUser = SeedUser();
        SeedProfile(pendingUser, isApproved: false, isSuspended: false);

        var suspendedUser = SeedUser();
        SeedProfile(suspendedUser, isApproved: true, isSuspended: true);

        var incompleteUser = SeedUser();
        // No profile

        var deletionUser = SeedUser(deletionRequestedAt: _clock.GetCurrentInstant());

        var missingConsentsUser = SeedUser();
        SeedProfile(missingConsentsUser, isApproved: true, isSuspended: false);
        // Has a required doc from above but no consent record

        var allIds = new[] { activeUser, pendingUser, suspendedUser, incompleteUser, deletionUser, missingConsentsUser };
        var result = await _service.PartitionUsersAsync(allIds, Xunit.TestContext.Current.CancellationToken);

        var totalCount = result.Active.Count
            + result.PendingApproval.Count
            + result.Suspended.Count
            + result.IncompleteSignup.Count
            + result.PendingDeletion.Count
            + result.MissingConsents.Count;

        totalCount.Should().Be(6);
    }

    [HumansFact]
    public async Task PartitionUsersAsync_NoBucketOverlap()
    {
        // Seed one user per category
        var activeUser = SeedUser();
        SeedProfile(activeUser, isApproved: true, isSuspended: false);
        var versionId = SeedRequiredVersion(SystemTeamIds.Volunteers);
        SeedConsent(activeUser, versionId);

        var pendingUser = SeedUser();
        SeedProfile(pendingUser, isApproved: false, isSuspended: false);

        var suspendedUser = SeedUser();
        SeedProfile(suspendedUser, isApproved: true, isSuspended: true);

        var incompleteUser = SeedUser();

        var deletionUser = SeedUser(deletionRequestedAt: _clock.GetCurrentInstant());

        var missingConsentsUser = SeedUser();
        SeedProfile(missingConsentsUser, isApproved: true, isSuspended: false);

        var allIds = new[] { activeUser, pendingUser, suspendedUser, incompleteUser, deletionUser, missingConsentsUser };
        var result = await _service.PartitionUsersAsync(allIds, Xunit.TestContext.Current.CancellationToken);

        var buckets = new[]
        {
            result.Active, result.PendingApproval, result.Suspended,
            result.IncompleteSignup, result.PendingDeletion, result.MissingConsents
        };

        // Verify no two buckets share any user ID
        for (var i = 0; i < buckets.Length; i++)
        {
            for (var j = i + 1; j < buckets.Length; j++)
            {
                buckets[i].Overlaps(buckets[j]).Should().BeFalse(
                    $"bucket {i} and bucket {j} should not overlap");
            }
        }
    }

    [HumansFact]
    public async Task PartitionUsersAsync_DeletionOverridesSuspended()
    {
        // User is both suspended AND has DeletionRequestedAt → should go to PendingDeletion
        var userId = SeedUser(deletionRequestedAt: _clock.GetCurrentInstant());
        SeedProfile(userId, isApproved: true, isSuspended: true);

        var result = await _service.PartitionUsersAsync([userId], Xunit.TestContext.Current.CancellationToken);

        result.PendingDeletion.Should().Contain(userId);
        result.Suspended.Should().NotContain(userId);
    }

    // --- Helpers ---

    private Guid SeedUser(Instant? deletionRequestedAt = null)
    {
        var userId = Guid.NewGuid();
        _usersById[userId] = new User
        {
            Id = userId,
            UserName = $"user-{userId}",
            Email = $"{userId}@test.com",
            DisplayName = $"User {userId.ToString()[..8]}",
            CreatedAt = _clock.GetCurrentInstant(),
            DeletionRequestedAt = deletionRequestedAt
        };
        return userId;
    }

    private void SeedProfile(Guid userId, bool isApproved, bool isSuspended)
    {
        _profilesByUserId[userId] = UserFixtures.Profile(
            burnerName: "Tester",
            firstName: "Test",
            lastName: "User",
            isApproved: isApproved,
            state: isSuspended ? ProfileState.Suspended : ProfileState.Active,
            createdAt: _clock.GetCurrentInstant());
    }

    private void SeedConsent(Guid userId, Guid versionId)
    {
        if (!_consentedVersionsByUser.TryGetValue(userId, out var set))
        {
            set = [];
            _consentedVersionsByUser[userId] = set;
        }
        set.Add(versionId);
    }

    private Guid SeedRequiredVersion(Guid teamId)
    {
        var now = _clock.GetCurrentInstant();
        var docId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var version = new RequiredDocumentVersionSnapshot(
            Id: versionId,
            LegalDocumentId: docId,
            LegalDocumentName: $"Doc-{docId}",
            LegalDocumentGracePeriodDays: 0,
            VersionNumber: "v1",
            EffectiveFrom: now - Duration.FromDays(1),
            RequiresReConsent: false,
            ChangesSummary: null);
        if (!_requiredVersionsByTeam.TryGetValue(teamId, out var list))
        {
            list = [];
            _requiredVersionsByTeam[teamId] = list;
        }
        list.Add(version);
        return versionId;
    }
}
