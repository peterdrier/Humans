using AwesomeAssertions;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories.Consent;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Testing;
using Xunit;

namespace Humans.Application.Tests.Repositories;

/// <summary>
/// Integration tests for <see cref="ConsentRepository"/>. Covers the
/// append-only write path and every read shape. Uses the shared
/// <see cref="TestDbContextFactory"/> so the repository sees a fresh context
/// per call while the test keeps one for seeding/verification.
/// </summary>
public sealed class ConsentRepositoryTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly ConsentRepository _repo;

    public ConsentRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new HumansDbContext(options);
        _clock = new FakeClock(Instant.FromUtc(2026, 3, 1, 12, 0));
        _repo = new ConsentRepository(new TestDbContextFactory(options));
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    // ==========================================================================
    // AddAsync
    // ==========================================================================

    [HumansFact]
    public async Task AddAsync_PersistsNewRecord()
    {
        var record = BuildRecord(Guid.NewGuid(), Guid.NewGuid());

        await _repo.AddAsync(record);

        var persisted = await _dbContext.ConsentRecords.AsNoTracking().FirstAsync();
        persisted.Id.Should().Be(record.Id);
        persisted.UserId.Should().Be(record.UserId);
        persisted.DocumentVersionId.Should().Be(record.DocumentVersionId);
    }

    [HumansFact]
    public async Task AddAsync_AutoSaves_PerCall()
    {
        // The old ConsentService added to a shared-scope DbContext and relied on
        // the caller's SaveChangesAsync. The new repository auto-saves each call,
        // so a subsequent read from a different context sees the row immediately.
        var record = BuildRecord(Guid.NewGuid(), Guid.NewGuid());

        await _repo.AddAsync(record);

        var count = await _dbContext.ConsentRecords.CountAsync();
        count.Should().Be(1);
    }

    // ==========================================================================
    // ExistsForUserAndVersionAsync
    // ==========================================================================

    [HumansFact]
    public async Task ExistsForUserAndVersionAsync_ReturnsTrue_WhenRecordExists()
    {
        var userId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        await _repo.AddAsync(BuildRecord(userId, versionId));

        var exists = await _repo.ExistsForUserAndVersionAsync(userId, versionId);

        exists.Should().BeTrue();
    }

    [HumansFact]
    public async Task ExistsForUserAndVersionAsync_ReturnsFalse_WhenNoRecord()
    {
        var exists = await _repo.ExistsForUserAndVersionAsync(Guid.NewGuid(), Guid.NewGuid());

        exists.Should().BeFalse();
    }

    [HumansFact]
    public async Task ExistsForUserAndVersionAsync_ReturnsFalse_ForDifferentUser()
    {
        var versionId = Guid.NewGuid();
        await _repo.AddAsync(BuildRecord(Guid.NewGuid(), versionId));

        var exists = await _repo.ExistsForUserAndVersionAsync(Guid.NewGuid(), versionId);

        exists.Should().BeFalse();
    }

    // ==========================================================================
    // GetByUserAndVersionAsync
    // ==========================================================================

    [HumansFact]
    public async Task GetByUserAndVersionAsync_ReturnsRecord_WhenExists()
    {
        var userId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        await _repo.AddAsync(BuildRecord(userId, versionId));

        var record = await _repo.GetByUserAndVersionAsync(userId, versionId);

        record.Should().NotBeNull();
        record!.UserId.Should().Be(userId);
        record.DocumentVersionId.Should().Be(versionId);
    }

    [HumansFact]
    public async Task GetByUserAndVersionAsync_ReturnsNull_WhenMissing()
    {
        var record = await _repo.GetByUserAndVersionAsync(Guid.NewGuid(), Guid.NewGuid());

        record.Should().BeNull();
    }

    // ==========================================================================
    // GetAllForUserAsync
    // ==========================================================================

    [HumansFact]
    public async Task GetAllForUserAsync_ReturnsOnlyThatUsersRecords_NewestFirst()
    {
        var userId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        var v1 = await SeedVersionAsync();
        var v2 = await SeedVersionAsync();

        var older = BuildRecord(userId, v1, consentedAt: _clock.GetCurrentInstant() - Duration.FromHours(2));
        var newer = BuildRecord(userId, v2, consentedAt: _clock.GetCurrentInstant() - Duration.FromMinutes(10));
        var otherUserRecord = BuildRecord(otherUserId, v1);

        await _repo.AddAsync(older);
        await _repo.AddAsync(newer);
        await _repo.AddAsync(otherUserRecord);

        var results = await _repo.GetAllForUserAsync(userId);

        results.Should().HaveCount(2);
        results[0].Id.Should().Be(newer.Id);
        results[1].Id.Should().Be(older.Id);
    }

    [HumansFact]
    public async Task GetAllForUserAsync_IncludesDocumentVersionAndLegalDocumentNavs()
    {
        var userId = Guid.NewGuid();
        var versionId = await SeedVersionAsync("Privacy Policy");

        await _repo.AddAsync(BuildRecord(userId, versionId));

        var results = await _repo.GetAllForUserAsync(userId);

        results.Should().HaveCount(1);
        results[0].DocumentVersion.Should().NotBeNull();
        results[0].DocumentVersion.LegalDocument.Should().NotBeNull();
        results[0].DocumentVersion.LegalDocument.Name.Should().Be("Privacy Policy");
    }

    [HumansFact]
    public async Task GetAllForUserAsync_ReturnsEmpty_WhenNoRecords()
    {
        var results = await _repo.GetAllForUserAsync(Guid.NewGuid());

        results.Should().BeEmpty();
    }

    // ==========================================================================
    // GetCountForUserAsync
    // ==========================================================================

    [HumansFact]
    public async Task GetCountForUserAsync_ReturnsCount()
    {
        var userId = Guid.NewGuid();
        await _repo.AddAsync(BuildRecord(userId, Guid.NewGuid()));
        await _repo.AddAsync(BuildRecord(userId, Guid.NewGuid()));
        await _repo.AddAsync(BuildRecord(Guid.NewGuid(), Guid.NewGuid())); // other user

        var count = await _repo.GetCountForUserAsync(userId);

        count.Should().Be(2);
    }

    [HumansFact]
    public async Task GetCountForUserAsync_ReturnsZero_WhenNoRecords()
    {
        var count = await _repo.GetCountForUserAsync(Guid.NewGuid());

        count.Should().Be(0);
    }

    // ==========================================================================
    // GetExplicitlyConsentedVersionIdsAsync
    // ==========================================================================

    [HumansFact]
    public async Task GetExplicitlyConsentedVersionIdsAsync_ReturnsOnlyExplicitConsents()
    {
        var userId = Guid.NewGuid();
        var explicitId = Guid.NewGuid();
        var implicitId = Guid.NewGuid();
        await _repo.AddAsync(BuildRecord(userId, explicitId, explicitConsent: true));
        await _repo.AddAsync(BuildRecord(userId, implicitId, explicitConsent: false));

        var ids = await _repo.GetExplicitlyConsentedVersionIdsAsync(userId);

        ids.Should().HaveCount(1);
        ids.Should().Contain(explicitId);
        ids.Should().NotContain(implicitId);
    }

    [HumansFact]
    public async Task GetExplicitlyConsentedVersionIdsAsync_ReturnsEmpty_WhenNone()
    {
        var ids = await _repo.GetExplicitlyConsentedVersionIdsAsync(Guid.NewGuid());

        ids.Should().BeEmpty();
    }

    // ==========================================================================
    // GetExplicitlyConsentedVersionIdsForUsersAsync
    // ==========================================================================

    [HumansFact]
    public async Task GetExplicitlyConsentedVersionIdsForUsersAsync_EmptyInput_ReturnsEmpty()
    {
        var map = await _repo.GetExplicitlyConsentedVersionIdsForUsersAsync(Array.Empty<Guid>());

        map.Should().BeEmpty();
    }

    [HumansFact]
    public async Task GetExplicitlyConsentedVersionIdsForUsersAsync_IncludesEveryInputUser()
    {
        // Every input user appears in the result, with an empty set when no consents.
        var userWithConsents = Guid.NewGuid();
        var userWithoutConsents = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        await _repo.AddAsync(BuildRecord(userWithConsents, versionId, explicitConsent: true));

        var map = await _repo.GetExplicitlyConsentedVersionIdsForUsersAsync(
            new List<Guid> { userWithConsents, userWithoutConsents });

        map.Should().ContainKey(userWithConsents);
        map.Should().ContainKey(userWithoutConsents);
        map[userWithConsents].Should().Contain(versionId);
        map[userWithoutConsents].Should().BeEmpty();
    }

    [HumansFact]
    public async Task GetExplicitlyConsentedVersionIdsForUsersAsync_ExcludesImplicitConsents()
    {
        var userId = Guid.NewGuid();
        var explicitVersion = Guid.NewGuid();
        var implicitVersion = Guid.NewGuid();
        await _repo.AddAsync(BuildRecord(userId, explicitVersion, explicitConsent: true));
        await _repo.AddAsync(BuildRecord(userId, implicitVersion, explicitConsent: false));

        var map = await _repo.GetExplicitlyConsentedVersionIdsForUsersAsync(new List<Guid> { userId });

        map[userId].Should().Contain(explicitVersion);
        map[userId].Should().NotContain(implicitVersion);
    }

    // ==========================================================================
    // GetPairsForUsersAndVersionsAsync
    // ==========================================================================

    [HumansFact]
    public async Task GetPairsForUsersAndVersionsAsync_EmptyUsers_ReturnsEmpty()
    {
        var pairs = await _repo.GetPairsForUsersAndVersionsAsync(
            Array.Empty<Guid>(), new[] { Guid.NewGuid() });

        pairs.Should().BeEmpty();
    }

    [HumansFact]
    public async Task GetPairsForUsersAndVersionsAsync_EmptyVersions_ReturnsEmpty()
    {
        var pairs = await _repo.GetPairsForUsersAndVersionsAsync(
            new[] { Guid.NewGuid() }, Array.Empty<Guid>());

        pairs.Should().BeEmpty();
    }

    [HumansFact]
    public async Task GetPairsForUsersAndVersionsAsync_ReturnsOnlyMatchingPairs()
    {
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var versionX = Guid.NewGuid();
        var versionY = Guid.NewGuid();
        var versionOutside = Guid.NewGuid();

        await _repo.AddAsync(BuildRecord(userA, versionX));
        await _repo.AddAsync(BuildRecord(userA, versionY));
        await _repo.AddAsync(BuildRecord(userB, versionX));
        await _repo.AddAsync(BuildRecord(userA, versionOutside));
        await _repo.AddAsync(BuildRecord(Guid.NewGuid(), versionX)); // different user

        var pairs = await _repo.GetPairsForUsersAndVersionsAsync(
            new[] { userA, userB }, new[] { versionX, versionY });

        pairs.Should().HaveCount(3);
        pairs.Should().Contain((userA, versionX));
        pairs.Should().Contain((userA, versionY));
        pairs.Should().Contain((userB, versionX));
    }

    // ==========================================================================
    // Helpers
    // ==========================================================================

    private async Task<Guid> SeedVersionAsync(string documentName = "Doc")
    {
        var teamId = Guid.NewGuid();
        var docId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var now = _clock.GetCurrentInstant();

        _dbContext.Teams.Add(new Team
        {
            Id = teamId,
            Name = "Test Team",
            Slug = "test-team",
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        });
        _dbContext.LegalDocuments.Add(new LegalDocument
        {
            Id = docId,
            Name = documentName,
            TeamId = teamId,
            IsActive = true,
            IsRequired = true,
            CurrentCommitSha = "abc",
            CreatedAt = now,
            LastSyncedAt = now
        });
        _dbContext.DocumentVersions.Add(new DocumentVersion
        {
            Id = versionId,
            LegalDocumentId = docId,
            VersionNumber = "v1",
            CommitSha = "abc",
            Content = new Dictionary<string, string>(StringComparer.Ordinal) { ["es"] = "text" },
            EffectiveFrom = now,
            CreatedAt = now
        });
        await _dbContext.SaveChangesAsync();
        return versionId;
    }

    private ConsentRecord BuildRecord(
        Guid userId,
        Guid documentVersionId,
        bool explicitConsent = true,
        Instant? consentedAt = null)
    {
        return new ConsentRecord
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            DocumentVersionId = documentVersionId,
            ConsentedAt = consentedAt ?? _clock.GetCurrentInstant(),
            IpAddress = "127.0.0.1",
            UserAgent = "test",
            ContentHash = "hash",
            ExplicitConsent = explicitConsent
        };
    }
}
