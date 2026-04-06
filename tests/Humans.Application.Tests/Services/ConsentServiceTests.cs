using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Services;
using Xunit;

namespace Humans.Application.Tests.Services;

public class ConsentServiceTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly ConsentService _service;
    private readonly IOnboardingService _onboardingService = Substitute.For<IOnboardingService>();
    private readonly IMembershipCalculator _membershipCalculator = Substitute.For<IMembershipCalculator>();
    private readonly INotificationInboxService _notificationInboxService = Substitute.For<INotificationInboxService>();
    private readonly ISystemTeamSync _syncJob = Substitute.For<ISystemTeamSync>();
    private readonly IHumansMetrics _metrics = Substitute.For<IHumansMetrics>();

    public ConsentServiceTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new HumansDbContext(options);
        _clock = new FakeClock(Instant.FromUtc(2026, 3, 1, 12, 0));
        _service = new ConsentService(
            _dbContext, _onboardingService, _membershipCalculator,
            _notificationInboxService, _syncJob, _metrics, _clock,
            NullLogger<ConsentService>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task SubmitConsentAsync_ValidConsent_CreatesRecord()
    {
        var userId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        SeedDocumentVersion(versionId, "Test Doc", new Dictionary<string, string>(StringComparer.Ordinal) { ["es"] = "Spanish text" });

        var result = await _service.SubmitConsentAsync(userId, versionId, true, "192.168.1.1", "TestAgent");

        result.Success.Should().BeTrue();
        var record = await _dbContext.ConsentRecords.FirstAsync();
        record.UserId.Should().Be(userId);
        record.DocumentVersionId.Should().Be(versionId);
        record.IpAddress.Should().Be("192.168.1.1");
        record.UserAgent.Should().Be("TestAgent");
        record.ExplicitConsent.Should().BeTrue();
        record.ConsentedAt.Should().Be(_clock.GetCurrentInstant());
    }

    [Fact]
    public async Task SubmitConsentAsync_ComputesCorrectSha256Hash()
    {
        var userId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        SeedDocumentVersion(versionId, "Test Doc", new Dictionary<string, string>(StringComparer.Ordinal) { ["es"] = "Spanish text" });

        await _service.SubmitConsentAsync(userId, versionId, true, "127.0.0.1", "Agent");

        var record = await _dbContext.ConsentRecords.FirstAsync();
        var expectedHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes("Spanish text"))).ToLowerInvariant();
        record.ContentHash.Should().Be(expectedHash);
    }

    [Fact]
    public async Task SubmitConsentAsync_AlreadyConsented_ReturnsError()
    {
        var userId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        SeedDocumentVersion(versionId, "Test Doc", new Dictionary<string, string>(StringComparer.Ordinal) { ["es"] = "text" });
        _dbContext.ConsentRecords.Add(new ConsentRecord
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            DocumentVersionId = versionId,
            ConsentedAt = _clock.GetCurrentInstant(),
            IpAddress = "127.0.0.1",
            UserAgent = "Agent",
            ContentHash = "abc",
            ExplicitConsent = true
        });
        await _dbContext.SaveChangesAsync();

        var result = await _service.SubmitConsentAsync(userId, versionId, true, "127.0.0.1", "Agent");

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("AlreadyConsented");
    }

    [Fact]
    public async Task SubmitConsentAsync_DocumentNotFound_ReturnsError()
    {
        var result = await _service.SubmitConsentAsync(Guid.NewGuid(), Guid.NewGuid(), true, "127.0.0.1", "Agent");

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("NotFound");
    }

    [Fact]
    public async Task SubmitConsentAsync_TruncatesLongUserAgent()
    {
        var userId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        SeedDocumentVersion(versionId, "Test Doc", new Dictionary<string, string>(StringComparer.Ordinal) { ["es"] = "text" });
        var longAgent = new string('A', 600);

        await _service.SubmitConsentAsync(userId, versionId, true, "127.0.0.1", longAgent);

        var record = await _dbContext.ConsentRecords.FirstAsync();
        record.UserAgent.Should().HaveLength(500);
    }

    [Fact]
    public async Task SubmitConsentAsync_CallsSetConsentCheckPending()
    {
        var userId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        SeedDocumentVersion(versionId, "Test Doc", new Dictionary<string, string>(StringComparer.Ordinal) { ["es"] = "text" });

        await _service.SubmitConsentAsync(userId, versionId, true, "127.0.0.1", "Agent");

        await _onboardingService.Received().SetConsentCheckPendingIfEligibleAsync(userId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SubmitConsentAsync_CallsSyncJobs()
    {
        var userId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        SeedDocumentVersion(versionId, "Test Doc", new Dictionary<string, string>(StringComparer.Ordinal) { ["es"] = "text" });

        await _service.SubmitConsentAsync(userId, versionId, true, "127.0.0.1", "Agent");

        await _syncJob.Received().SyncVolunteersMembershipForUserAsync(userId, Arg.Any<CancellationToken>());
        await _syncJob.Received().SyncCoordinatorsMembershipForUserAsync(userId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SubmitConsentAsync_RecordsMetric()
    {
        var userId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        SeedDocumentVersion(versionId, "Test Doc", new Dictionary<string, string>(StringComparer.Ordinal) { ["es"] = "text" });

        await _service.SubmitConsentAsync(userId, versionId, true, "127.0.0.1", "Agent");

        _metrics.Received().RecordConsentGiven();
    }

    [Fact]
    public async Task SubmitConsentAsync_ReturnsDocumentName()
    {
        var userId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        SeedDocumentVersion(versionId, "Privacy Policy", new Dictionary<string, string>(StringComparer.Ordinal) { ["es"] = "text" });

        var result = await _service.SubmitConsentAsync(userId, versionId, true, "127.0.0.1", "Agent");

        result.DocumentName.Should().Be("Privacy Policy");
    }

    // --- GetConsentDashboardAsync ---

    [Fact]
    public async Task GetConsentDashboardAsync_ReturnsDocumentsGroupedByTeam()
    {
        var userId = Guid.NewGuid();
        var teamId1 = Guid.NewGuid();
        var teamId2 = Guid.NewGuid();
        _membershipCalculator.GetRequiredTeamIdsForUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { teamId1, teamId2 });

        SeedTeam(teamId1, "Team A");
        SeedTeam(teamId2, "Team B");
        SeedDocument(teamId1, "Doc A");
        SeedDocument(teamId2, "Doc B");
        await _dbContext.SaveChangesAsync();

        var (groups, _) = await _service.GetConsentDashboardAsync(userId);

        groups.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetConsentDashboardAsync_OnlyIncludesActiveRequiredDocuments()
    {
        var userId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        _membershipCalculator.GetRequiredTeamIdsForUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { teamId });

        SeedTeam(teamId, "Team");
        SeedDocument(teamId, "Active Doc");
        SeedDocument(teamId, "Inactive Doc", isActive: false);
        await _dbContext.SaveChangesAsync();

        var (groups, _) = await _service.GetConsentDashboardAsync(userId);

        groups.Should().HaveCount(1);
        groups[0].Documents.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetConsentDashboardAsync_SelectsCurrentVersion()
    {
        var userId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var now = _clock.GetCurrentInstant();
        _membershipCalculator.GetRequiredTeamIdsForUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { teamId });

        SeedTeam(teamId, "Team");
        var docId = Guid.NewGuid();
        var olderVersionId = Guid.NewGuid();
        var newerVersionId = Guid.NewGuid();
        _dbContext.LegalDocuments.Add(new LegalDocument
        {
            Id = docId,
            Name = "Versioned Doc",
            TeamId = teamId,
            IsRequired = true,
            IsActive = true,
            CurrentCommitSha = "test",
            CreatedAt = now,
            LastSyncedAt = now
        });
        _dbContext.DocumentVersions.Add(new DocumentVersion
        {
            Id = olderVersionId,
            LegalDocumentId = docId,
            VersionNumber = "v1",
            CommitSha = "old",
            EffectiveFrom = now - Duration.FromDays(30),
            Content = new Dictionary<string, string>(StringComparer.Ordinal) { ["es"] = "old" },
            CreatedAt = now
        });
        _dbContext.DocumentVersions.Add(new DocumentVersion
        {
            Id = newerVersionId,
            LegalDocumentId = docId,
            VersionNumber = "v2",
            CommitSha = "new",
            EffectiveFrom = now - Duration.FromDays(1),
            Content = new Dictionary<string, string>(StringComparer.Ordinal) { ["es"] = "new" },
            CreatedAt = now
        });
        await _dbContext.SaveChangesAsync();

        var (groups, _) = await _service.GetConsentDashboardAsync(userId);

        groups.Should().HaveCount(1);
        groups[0].Documents.Should().HaveCount(1);
        groups[0].Documents[0].Version.Id.Should().Be(newerVersionId);
    }

    [Fact]
    public async Task GetConsentDashboardAsync_PairsVersionWithConsent()
    {
        var userId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        _membershipCalculator.GetRequiredTeamIdsForUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { teamId });

        SeedTeam(teamId, "Team");
        var versionId = SeedDocument(teamId, "Doc");
        SeedConsentRecord(userId, versionId);
        await _dbContext.SaveChangesAsync();

        var (groups, _) = await _service.GetConsentDashboardAsync(userId);

        groups.Should().HaveCount(1);
        groups[0].Documents[0].Consent.Should().NotBeNull();
    }

    [Fact]
    public async Task GetConsentDashboardAsync_NullConsentWhenNotSigned()
    {
        var userId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        _membershipCalculator.GetRequiredTeamIdsForUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { teamId });

        SeedTeam(teamId, "Team");
        SeedDocument(teamId, "Doc");
        await _dbContext.SaveChangesAsync();

        var (groups, _) = await _service.GetConsentDashboardAsync(userId);

        groups.Should().HaveCount(1);
        groups[0].Documents[0].Consent.Should().BeNull();
    }

    [Fact]
    public async Task GetConsentDashboardAsync_ReturnsHistory()
    {
        var userId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var now = _clock.GetCurrentInstant();
        _membershipCalculator.GetRequiredTeamIdsForUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { teamId });

        SeedTeam(teamId, "Team");
        var v1 = SeedDocument(teamId, "Doc A");
        var v2 = SeedDocument(teamId, "Doc B");
        SeedConsentRecord(userId, v1, now - Duration.FromHours(2));
        SeedConsentRecord(userId, v2, now - Duration.FromHours(1));
        await _dbContext.SaveChangesAsync();

        var (_, history) = await _service.GetConsentDashboardAsync(userId);

        history.Should().HaveCount(2);
        history[0].ConsentedAt.Should().BeGreaterThan(history[1].ConsentedAt);
    }

    [Fact]
    public async Task GetConsentDashboardAsync_ExcludesFutureVersions()
    {
        var userId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var now = _clock.GetCurrentInstant();
        _membershipCalculator.GetRequiredTeamIdsForUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { teamId });

        SeedTeam(teamId, "Team");
        SeedDocument(teamId, "Future Doc", effectiveFrom: now + Duration.FromDays(30));
        await _dbContext.SaveChangesAsync();

        var (groups, _) = await _service.GetConsentDashboardAsync(userId);

        groups.Should().HaveCount(1);
        groups[0].Documents.Should().BeEmpty();
    }

    [Fact]
    public async Task GetConsentDashboardAsync_EmptyWhenNoDocuments()
    {
        var userId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        _membershipCalculator.GetRequiredTeamIdsForUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { teamId });

        var (groups, history) = await _service.GetConsentDashboardAsync(userId);

        groups.Should().BeEmpty();
        history.Should().BeEmpty();
    }

    // --- GetConsentReviewDetailAsync ---

    [Fact]
    public async Task GetConsentReviewDetailAsync_ReturnsVersionWithDocumentAndConsent()
    {
        var userId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        SeedDocumentVersion(versionId, "Test Doc", new Dictionary<string, string>(StringComparer.Ordinal) { ["es"] = "text" });
        SeedConsentRecord(userId, versionId);
        _dbContext.Profiles.Add(new Profile
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            BurnerName = "Test",
            FirstName = "Jane",
            LastName = "Doe",
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        });
        await _dbContext.SaveChangesAsync();

        var (version, consent, fullName) = await _service.GetConsentReviewDetailAsync(versionId, userId);

        version.Should().NotBeNull();
        version!.LegalDocument.Should().NotBeNull();
        consent.Should().NotBeNull();
        fullName.Should().Be("Jane Doe");
    }

    [Fact]
    public async Task GetConsentReviewDetailAsync_NullConsentWhenNotSigned()
    {
        var userId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        SeedDocumentVersion(versionId, "Test Doc", new Dictionary<string, string>(StringComparer.Ordinal) { ["es"] = "text" });
        _dbContext.Profiles.Add(new Profile
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            BurnerName = "Test",
            FirstName = "Jane",
            LastName = "Doe",
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        });
        await _dbContext.SaveChangesAsync();

        var (version, consent, fullName) = await _service.GetConsentReviewDetailAsync(versionId, userId);

        version.Should().NotBeNull();
        consent.Should().BeNull();
        fullName.Should().Be("Jane Doe");
    }

    [Fact]
    public async Task GetConsentReviewDetailAsync_VersionNotFound_ReturnsAllNulls()
    {
        var (version, consent, fullName) = await _service.GetConsentReviewDetailAsync(Guid.NewGuid(), Guid.NewGuid());

        version.Should().BeNull();
        consent.Should().BeNull();
        fullName.Should().BeNull();
    }

    [Fact]
    public async Task GetConsentReviewDetailAsync_NullFullNameWhenNoProfile()
    {
        var userId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        SeedDocumentVersion(versionId, "Test Doc", new Dictionary<string, string>(StringComparer.Ordinal) { ["es"] = "text" });

        var (version, consent, fullName) = await _service.GetConsentReviewDetailAsync(versionId, userId);

        version.Should().NotBeNull();
        consent.Should().BeNull();
        fullName.Should().BeNull();
    }

    // --- Helpers ---

    private Team SeedTeam(Guid teamId, string name)
    {
        var team = new Team
        {
            Id = teamId,
            Name = name,
            Slug = name.ToLowerInvariant().Replace(' ', '-'),
            IsActive = true,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        };
        _dbContext.Teams.Add(team);
        return team;
    }

    private Guid SeedDocument(Guid teamId, string name, bool isActive = true, bool isRequired = true, Instant? effectiveFrom = null)
    {
        var now = _clock.GetCurrentInstant();
        var docId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        _dbContext.LegalDocuments.Add(new LegalDocument
        {
            Id = docId,
            Name = name,
            TeamId = teamId,
            IsRequired = isRequired,
            IsActive = isActive,
            CurrentCommitSha = "test",
            CreatedAt = now,
            LastSyncedAt = now
        });
        _dbContext.DocumentVersions.Add(new DocumentVersion
        {
            Id = versionId,
            LegalDocumentId = docId,
            VersionNumber = "v1",
            CommitSha = "abc123",
            Content = new Dictionary<string, string>(StringComparer.Ordinal) { ["es"] = "text" },
            EffectiveFrom = effectiveFrom ?? now - Duration.FromDays(1),
            CreatedAt = now
        });
        return versionId;
    }

    private void SeedConsentRecord(Guid userId, Guid versionId, Instant? consentedAt = null)
    {
        _dbContext.ConsentRecords.Add(new ConsentRecord
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            DocumentVersionId = versionId,
            ExplicitConsent = true,
            ConsentedAt = consentedAt ?? _clock.GetCurrentInstant(),
            IpAddress = "127.0.0.1",
            UserAgent = "test",
            ContentHash = "testhash"
        });
    }

    private void SeedDocumentVersion(Guid versionId, string documentName, Dictionary<string, string> content)
    {
        var teamId = Guid.NewGuid();
        _dbContext.Teams.Add(new Team
        {
            Id = teamId,
            Name = "Volunteers",
            Slug = "volunteers",
            IsActive = true,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        });
        var docId = Guid.NewGuid();
        _dbContext.LegalDocuments.Add(new LegalDocument
        {
            Id = docId,
            Name = documentName,
            TeamId = teamId,
            IsRequired = true,
            IsActive = true,
            CurrentCommitSha = "abc123",
            CreatedAt = _clock.GetCurrentInstant(),
            LastSyncedAt = _clock.GetCurrentInstant()
        });
        _dbContext.DocumentVersions.Add(new DocumentVersion
        {
            Id = versionId,
            LegalDocumentId = docId,
            VersionNumber = "1.0",
            CommitSha = "abc123",
            Content = content,
            EffectiveFrom = _clock.GetCurrentInstant() - Duration.FromDays(1),
            CreatedAt = _clock.GetCurrentInstant()
        });
        _dbContext.SaveChanges();
    }
}
