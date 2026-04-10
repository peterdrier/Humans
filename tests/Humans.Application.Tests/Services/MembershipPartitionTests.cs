using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Services;
using Xunit;

namespace Humans.Application.Tests.Services;

public class MembershipPartitionTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly MembershipCalculator _service;
    private readonly IConsentService _consentService = Substitute.For<IConsentService>();
    private readonly ILegalDocumentSyncService _legalDocumentSyncService = Substitute.For<ILegalDocumentSyncService>();

    public MembershipPartitionTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new HumansDbContext(options);
        _clock = new FakeClock(Instant.FromUtc(2026, 3, 1, 12, 0));

        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IConsentService)).Returns(_consentService);

        _service = new MembershipCalculator(_dbContext, serviceProvider, _legalDocumentSyncService, _clock);

        // Delegate to in-memory DB so seeded data is returned
        _legalDocumentSyncService.GetRequiredDocumentVersionsForTeamAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var teamId = callInfo.Arg<Guid>();
                var now = _clock.GetCurrentInstant();
                var versions = _dbContext.LegalDocuments
                    .Where(d => d.IsRequired && d.IsActive && d.TeamId == teamId)
                    .SelectMany(d => d.Versions)
                    .Where(v => v.EffectiveFrom <= now)
                    .Include(v => v.LegalDocument)
                    .ToList()
                    .GroupBy(v => v.LegalDocumentId)
                    .Select(g => g.OrderByDescending(v => v.EffectiveFrom).First())
                    .ToList();
                return Task.FromResult<IReadOnlyList<DocumentVersion>>(versions);
            });

        _consentService.GetConsentedVersionIdsAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var userId = callInfo.Arg<Guid>();
                var ids = _dbContext.ConsentRecords
                    .Where(cr => cr.UserId == userId && cr.ExplicitConsent)
                    .Select(cr => cr.DocumentVersionId)
                    .ToHashSet();
                return Task.FromResult<IReadOnlySet<Guid>>(ids);
            });

        _consentService.GetConsentMapForUsersAsync(
            Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var userIds = callInfo.Arg<IReadOnlyList<Guid>>();
                var consents = _dbContext.ConsentRecords
                    .Where(cr => userIds.Contains(cr.UserId) && cr.ExplicitConsent)
                    .Select(cr => new { cr.UserId, cr.DocumentVersionId })
                    .ToList();
                var result = userIds.ToDictionary(
                    id => id,
                    _ => (IReadOnlySet<Guid>)new HashSet<Guid>());
                foreach (var c in consents)
                {
                    ((HashSet<Guid>)result[c.UserId]).Add(c.DocumentVersionId);
                }
                return Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlySet<Guid>>>(result);
            });

        // Seed the Volunteers system team (needed for consent queries)
        _dbContext.Teams.Add(new Team
        {
            Id = SystemTeamIds.Volunteers,
            Name = "Volunteers",
            Slug = "volunteers",
            SystemTeamType = SystemTeamType.Volunteers,
            IsActive = true,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        });
        _dbContext.SaveChanges();
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task PartitionUsersAsync_ActiveUser_GoesToActiveBucket()
    {
        var userId = SeedUser();
        SeedProfile(userId, isApproved: true, isSuspended: false);
        var versionId = SeedLegalDocumentWithVersion(SystemTeamIds.Volunteers);
        SeedConsentRecord(userId, versionId);
        await _dbContext.SaveChangesAsync();

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
        await _dbContext.SaveChangesAsync();

        var result = await _service.PartitionUsersAsync(new[] { userId });

        result.PendingApproval.Should().Contain(userId);
        result.Active.Should().NotContain(userId);
    }

    [Fact]
    public async Task PartitionUsersAsync_Suspended_GoesToSuspendedBucket()
    {
        var userId = SeedUser();
        SeedProfile(userId, isApproved: true, isSuspended: true);
        await _dbContext.SaveChangesAsync();

        var result = await _service.PartitionUsersAsync(new[] { userId });

        result.Suspended.Should().Contain(userId);
        result.Active.Should().NotContain(userId);
    }

    [Fact]
    public async Task PartitionUsersAsync_IncompleteSignup_GoesToIncompleteSignupBucket()
    {
        var userId = SeedUser();
        // No profile seeded
        await _dbContext.SaveChangesAsync();

        var result = await _service.PartitionUsersAsync(new[] { userId });

        result.IncompleteSignup.Should().Contain(userId);
        result.Active.Should().NotContain(userId);
    }

    [Fact]
    public async Task PartitionUsersAsync_PendingDeletion_GoesToPendingDeletionBucket()
    {
        var userId = SeedUser(deletionRequestedAt: _clock.GetCurrentInstant());
        SeedProfile(userId, isApproved: true, isSuspended: false);
        await _dbContext.SaveChangesAsync();

        var result = await _service.PartitionUsersAsync(new[] { userId });

        result.PendingDeletion.Should().Contain(userId);
        result.Active.Should().NotContain(userId);
    }

    [Fact]
    public async Task PartitionUsersAsync_MissingConsents_GoesToMissingConsentsBucket()
    {
        var userId = SeedUser();
        SeedProfile(userId, isApproved: true, isSuspended: false);
        SeedLegalDocumentWithVersion(SystemTeamIds.Volunteers); // required doc, no consent record
        await _dbContext.SaveChangesAsync();

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
        var versionId = SeedLegalDocumentWithVersion(SystemTeamIds.Volunteers);
        SeedConsentRecord(activeUser, versionId);

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

        await _dbContext.SaveChangesAsync();

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
        var versionId = SeedLegalDocumentWithVersion(SystemTeamIds.Volunteers);
        SeedConsentRecord(activeUser, versionId);

        var pendingUser = SeedUser();
        SeedProfile(pendingUser, isApproved: false, isSuspended: false);

        var suspendedUser = SeedUser();
        SeedProfile(suspendedUser, isApproved: true, isSuspended: true);

        var incompleteUser = SeedUser();

        var deletionUser = SeedUser(deletionRequestedAt: _clock.GetCurrentInstant());

        var missingConsentsUser = SeedUser();
        SeedProfile(missingConsentsUser, isApproved: true, isSuspended: false);

        await _dbContext.SaveChangesAsync();

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
        await _dbContext.SaveChangesAsync();

        var result = await _service.PartitionUsersAsync(new[] { userId });

        result.PendingDeletion.Should().Contain(userId);
        result.Suspended.Should().NotContain(userId);
    }

    // --- Helpers ---

    private Guid SeedUser(Instant? deletionRequestedAt = null)
    {
        var userId = Guid.NewGuid();
        _dbContext.Users.Add(new User
        {
            Id = userId,
            UserName = $"user-{userId}",
            Email = $"{userId}@test.com",
            DisplayName = $"User {userId.ToString()[..8]}",
            CreatedAt = _clock.GetCurrentInstant(),
            DeletionRequestedAt = deletionRequestedAt
        });
        return userId;
    }

    private void SeedProfile(Guid userId, bool isApproved, bool isSuspended)
    {
        _dbContext.Profiles.Add(new Profile
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
        });
    }

    private void SeedConsentRecord(Guid userId, Guid versionId)
    {
        _dbContext.ConsentRecords.Add(new ConsentRecord
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            DocumentVersionId = versionId,
            ExplicitConsent = true,
            ConsentedAt = _clock.GetCurrentInstant(),
            IpAddress = "127.0.0.1",
            UserAgent = "test",
            ContentHash = "testhash"
        });
    }

    private Guid SeedLegalDocumentWithVersion(Guid teamId)
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
        _dbContext.LegalDocuments.Add(doc);
        _dbContext.DocumentVersions.Add(new DocumentVersion
        {
            Id = versionId,
            LegalDocumentId = docId,
            VersionNumber = "v1",
            CommitSha = "abc123",
            EffectiveFrom = now - Duration.FromDays(1),
            RequiresReConsent = false,
            CreatedAt = now,
            LegalDocument = doc
        });
        return versionId;
    }
}
