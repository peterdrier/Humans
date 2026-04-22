using AwesomeAssertions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Humans.Application.Interfaces;
using Humans.Application.Services.Governance;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Xunit;

namespace Humans.Application.Tests.Services;

public class MembershipPartitionTests
{
    private readonly FakeClock _clock;
    private readonly MembershipCalculator _service;
    private readonly IProfileService _profileService = Substitute.For<IProfileService>();
    private readonly ITeamService _teamService = Substitute.For<ITeamService>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IRoleAssignmentService _roleAssignmentService = Substitute.For<IRoleAssignmentService>();
    private readonly IConsentService _consentService = Substitute.For<IConsentService>();
    private readonly ILegalDocumentSyncService _legalDocumentSyncService = Substitute.For<ILegalDocumentSyncService>();

    private readonly Dictionary<Guid, User> _usersById = new();
    private readonly Dictionary<Guid, Profile> _profilesByUserId = new();
    private readonly Dictionary<Guid, List<DocumentVersion>> _requiredVersionsByTeam = new();
    private readonly Dictionary<Guid, HashSet<Guid>> _consentedVersionsByUser = new();

    public MembershipPartitionTests()
    {
        _clock = new FakeClock(Instant.FromUtc(2026, 3, 1, 12, 0));

        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IConsentService)).Returns(_consentService);

        _service = new MembershipCalculator(
            _profileService,
            _teamService,
            _userService,
            _roleAssignmentService,
            _legalDocumentSyncService,
            serviceProvider,
            _clock);

        _userService.GetByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var ids = ci.Arg<IReadOnlyCollection<Guid>>();
                var map = ids
                    .Where(_usersById.ContainsKey)
                    .ToDictionary(id => id, id => _usersById[id]);
                return Task.FromResult<IReadOnlyDictionary<Guid, User>>(map);
            });

        _profileService.GetByUserIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var ids = ci.Arg<IReadOnlyCollection<Guid>>();
                var map = ids
                    .Where(_profilesByUserId.ContainsKey)
                    .ToDictionary(id => id, id => _profilesByUserId[id]);
                return Task.FromResult<IReadOnlyDictionary<Guid, Profile>>(map);
            });

        _legalDocumentSyncService.GetRequiredDocumentVersionsForTeamAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var teamId = ci.Arg<Guid>();
                var versions = _requiredVersionsByTeam.GetValueOrDefault(teamId) ?? new();
                return Task.FromResult<IReadOnlyList<DocumentVersion>>(versions);
            });

        _consentService.GetConsentMapForUsersAsync(
            Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var userIds = ci.Arg<IReadOnlyList<Guid>>();
                var result = userIds.ToDictionary(
                    id => id,
                    id => (IReadOnlySet<Guid>)(_consentedVersionsByUser.GetValueOrDefault(id) ?? new HashSet<Guid>()));
                return Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlySet<Guid>>>(result);
            });
    }

    [Fact]
    public async Task PartitionUsersAsync_ActiveUser_GoesToActiveBucket()
    {
        var userId = SeedUser();
        SeedProfile(userId, isApproved: true, isSuspended: false);
        var versionId = SeedRequiredVersion(SystemTeamIds.Volunteers);
        SeedConsent(userId, versionId);

        var result = await _service.PartitionUsersAsync(new[] { userId });

        result.Active.Should().Contain(userId);
        result.PendingApproval.Should().NotContain(userId);
        result.Suspended.Should().NotContain(userId);
        result.IncompleteSignup.Should().NotContain(userId);
        result.PendingDeletion.Should().NotContain(userId);
        result.MissingConsents.Should().NotContain(userId);
    }

    [Fact]
    public async Task PartitionUsersAsync_PendingApproval_GoesToPendingApprovalBucket()
    {
        var userId = SeedUser();
        SeedProfile(userId, isApproved: false, isSuspended: false);

        var result = await _service.PartitionUsersAsync(new[] { userId });

        result.PendingApproval.Should().Contain(userId);
        result.Active.Should().NotContain(userId);
    }

    [Fact]
    public async Task PartitionUsersAsync_Suspended_GoesToSuspendedBucket()
    {
        var userId = SeedUser();
        SeedProfile(userId, isApproved: true, isSuspended: true);

        var result = await _service.PartitionUsersAsync(new[] { userId });

        result.Suspended.Should().Contain(userId);
        result.Active.Should().NotContain(userId);
    }

    [Fact]
    public async Task PartitionUsersAsync_IncompleteSignup_GoesToIncompleteSignupBucket()
    {
        var userId = SeedUser();
        // No profile seeded

        var result = await _service.PartitionUsersAsync(new[] { userId });

        result.IncompleteSignup.Should().Contain(userId);
        result.Active.Should().NotContain(userId);
    }

    [Fact]
    public async Task PartitionUsersAsync_PendingDeletion_GoesToPendingDeletionBucket()
    {
        var userId = SeedUser(deletionRequestedAt: _clock.GetCurrentInstant());
        SeedProfile(userId, isApproved: true, isSuspended: false);

        var result = await _service.PartitionUsersAsync(new[] { userId });

        result.PendingDeletion.Should().Contain(userId);
        result.Active.Should().NotContain(userId);
    }

    [Fact]
    public async Task PartitionUsersAsync_MissingConsents_GoesToMissingConsentsBucket()
    {
        var userId = SeedUser();
        SeedProfile(userId, isApproved: true, isSuspended: false);
        SeedRequiredVersion(SystemTeamIds.Volunteers); // required doc, no consent record

        var result = await _service.PartitionUsersAsync(new[] { userId });

        result.MissingConsents.Should().Contain(userId);
        result.Active.Should().NotContain(userId);
    }

    [Fact]
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
        var result = await _service.PartitionUsersAsync(allIds);

        var totalCount = result.Active.Count
            + result.PendingApproval.Count
            + result.Suspended.Count
            + result.IncompleteSignup.Count
            + result.PendingDeletion.Count
            + result.MissingConsents.Count;

        totalCount.Should().Be(6);
    }

    [Fact]
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
        var result = await _service.PartitionUsersAsync(allIds);

        var buckets = new HashSet<Guid>[]
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

    [Fact]
    public async Task PartitionUsersAsync_DeletionOverridesSuspended()
    {
        // User is both suspended AND has DeletionRequestedAt → should go to PendingDeletion
        var userId = SeedUser(deletionRequestedAt: _clock.GetCurrentInstant());
        SeedProfile(userId, isApproved: true, isSuspended: true);

        var result = await _service.PartitionUsersAsync(new[] { userId });

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
        _profilesByUserId[userId] = new Profile
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            BurnerName = "Tester",
            FirstName = "Test",
            LastName = "User",
            IsApproved = isApproved,
            IsSuspended = isSuspended,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        };
    }

    private void SeedConsent(Guid userId, Guid versionId)
    {
        if (!_consentedVersionsByUser.TryGetValue(userId, out var set))
        {
            set = new HashSet<Guid>();
            _consentedVersionsByUser[userId] = set;
        }
        set.Add(versionId);
    }

    private Guid SeedRequiredVersion(Guid teamId)
    {
        var now = _clock.GetCurrentInstant();
        var docId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var doc = new LegalDocument
        {
            Id = docId,
            Name = $"Doc-{docId}",
            TeamId = teamId,
            IsRequired = true,
            IsActive = true,
            GracePeriodDays = 0,
            CurrentCommitSha = "test",
            CreatedAt = now,
            LastSyncedAt = now
        };
        var version = new DocumentVersion
        {
            Id = versionId,
            LegalDocumentId = docId,
            VersionNumber = "v1",
            CommitSha = "abc123",
            EffectiveFrom = now - Duration.FromDays(1),
            RequiresReConsent = false,
            CreatedAt = now,
            LegalDocument = doc
        };
        if (!_requiredVersionsByTeam.TryGetValue(teamId, out var list))
        {
            list = new List<DocumentVersion>();
            _requiredVersionsByTeam[teamId] = list;
        }
        list.Add(version);
        return versionId;
    }
}
