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
            _syncJob, _metrics, _clock,
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
        SeedDocumentVersion(versionId, "Test Doc", new Dictionary<string, string>(StringComparer.Ordinal) { ["es"] ="Spanish text" });

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
        SeedDocumentVersion(versionId, "Test Doc", new Dictionary<string, string>(StringComparer.Ordinal) { ["es"] ="Spanish text" });

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
        SeedDocumentVersion(versionId, "Test Doc", new Dictionary<string, string>(StringComparer.Ordinal) { ["es"] ="text" });
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
        SeedDocumentVersion(versionId, "Test Doc", new Dictionary<string, string>(StringComparer.Ordinal) { ["es"] ="text" });
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
        SeedDocumentVersion(versionId, "Test Doc", new Dictionary<string, string>(StringComparer.Ordinal) { ["es"] ="text" });

        await _service.SubmitConsentAsync(userId, versionId, true, "127.0.0.1", "Agent");

        await _onboardingService.Received().SetConsentCheckPendingIfEligibleAsync(userId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SubmitConsentAsync_CallsSyncJobs()
    {
        var userId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        SeedDocumentVersion(versionId, "Test Doc", new Dictionary<string, string>(StringComparer.Ordinal) { ["es"] ="text" });

        await _service.SubmitConsentAsync(userId, versionId, true, "127.0.0.1", "Agent");

        await _syncJob.Received().SyncVolunteersMembershipForUserAsync(userId, Arg.Any<CancellationToken>());
        await _syncJob.Received().SyncLeadsMembershipForUserAsync(userId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SubmitConsentAsync_RecordsMetric()
    {
        var userId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        SeedDocumentVersion(versionId, "Test Doc", new Dictionary<string, string>(StringComparer.Ordinal) { ["es"] ="text" });

        await _service.SubmitConsentAsync(userId, versionId, true, "127.0.0.1", "Agent");

        _metrics.Received().RecordConsentGiven();
    }

    [Fact]
    public async Task SubmitConsentAsync_ReturnsDocumentName()
    {
        var userId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        SeedDocumentVersion(versionId, "Privacy Policy", new Dictionary<string, string>(StringComparer.Ordinal) { ["es"] ="text" });

        var result = await _service.SubmitConsentAsync(userId, versionId, true, "127.0.0.1", "Agent");

        result.DocumentName.Should().Be("Privacy Policy");
    }

    // --- Helpers ---

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
