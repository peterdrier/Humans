using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using Humans.Application.DTOs;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Services;
using Xunit;

namespace Humans.Application.Tests.Services;

public class AdminLegalDocumentServiceTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly FakeLegalDocumentSyncService _syncService;
    private readonly AdminLegalDocumentService _service;
    private readonly Team _team;

    public AdminLegalDocumentServiceTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new HumansDbContext(options);
        _clock = new FakeClock(Instant.FromUtc(2026, 2, 15, 18, 0));
        _syncService = new FakeLegalDocumentSyncService();
        _service = new AdminLegalDocumentService(
            _dbContext,
            _syncService,
            Options.Create(new GitHubSettings
            {
                Owner = "owner",
                Repository = "repo",
                Branch = "main"
            }),
            _clock);

        _team = new Team
        {
            Id = Guid.NewGuid(),
            Name = "Volunteers",
            Slug = "volunteers",
            IsActive = true,
            SystemTeamType = SystemTeamType.None,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        };

        _dbContext.Teams.Add(_team);
        _dbContext.SaveChanges();
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void NormalizeGitHubFolderPath_PlainPath_ReturnsNormalizedFolder()
    {
        var result = _service.NormalizeGitHubFolderPath("Volunteer");

        result.IsValid.Should().BeTrue();
        result.NormalizedFolderPath.Should().Be("Volunteer/");
        result.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void NormalizeGitHubFolderPath_WrongRepository_ReturnsValidationError()
    {
        var result = _service.NormalizeGitHubFolderPath("https://github.com/another/repo/tree/main/Volunteer");

        result.IsValid.Should().BeFalse();
        result.NormalizedFolderPath.Should().BeNull();
        result.ErrorMessage.Should().Contain("configured repository is owner/repo");
    }

    [Fact]
    public async Task CreateLegalDocumentAsync_PersistsAndReturnsDocument()
    {
        var request = new AdminLegalDocumentUpsertRequest(
            "Privacy Policy",
            _team.Id,
            true,
            true,
            7,
            "privacy/");

        var document = await _service.CreateLegalDocumentAsync(request);
        var documents = await _service.GetLegalDocumentsAsync(_team.Id);

        document.Name.Should().Be("Privacy Policy");
        documents.Should().ContainSingle();
        documents[0].Team.Name.Should().Be("Volunteers");
        documents[0].GitHubFolderPath.Should().Be("privacy/");
    }

    [Fact]
    public async Task UpdateVersionSummaryAsync_TrimsAndPersistsSummary()
    {
        var document = await SeedDocumentAsync("Code of Conduct");
        var versionId = Guid.NewGuid();

        _dbContext.DocumentVersions.Add(new DocumentVersion
        {
            Id = versionId,
            LegalDocumentId = document.Id,
            VersionNumber = "v1",
            CommitSha = "abc123",
            EffectiveFrom = _clock.GetCurrentInstant(),
            CreatedAt = _clock.GetCurrentInstant(),
            LegalDocument = document
        });
        await _dbContext.SaveChangesAsync();

        var updated = await _service.UpdateVersionSummaryAsync(document.Id, versionId, "  Clarified scope  ");
        var version = await _dbContext.DocumentVersions.FindAsync(versionId);

        updated.Should().BeTrue();
        version.Should().NotBeNull();
        version!.ChangesSummary.Should().Be("Clarified scope");
    }

    [Fact]
    public async Task SyncLegalDocumentAsync_DelegatesToSyncService()
    {
        var document = await SeedDocumentAsync("Privacy");
        _syncService.SyncResult = "updated 1 file";

        var result = await _service.SyncLegalDocumentAsync(document.Id);

        result.Should().Be("updated 1 file");
        _syncService.LastSyncedDocumentId.Should().Be(document.Id);
    }

    private async Task<LegalDocument> SeedDocumentAsync(string name)
    {
        var document = new LegalDocument
        {
            Id = Guid.NewGuid(),
            Name = name,
            TeamId = _team.Id,
            IsRequired = true,
            IsActive = true,
            GracePeriodDays = 0,
            CurrentCommitSha = "seed",
            GitHubFolderPath = $"{name.ToLowerInvariant()}/",
            CreatedAt = _clock.GetCurrentInstant(),
            LastSyncedAt = _clock.GetCurrentInstant()
        };

        _dbContext.LegalDocuments.Add(document);
        await _dbContext.SaveChangesAsync();
        return document;
    }

    private sealed class FakeLegalDocumentSyncService : ILegalDocumentSyncService
    {
        public Guid? LastSyncedDocumentId { get; private set; }
        public string? SyncResult { get; set; }

        public Task<IReadOnlyList<LegalDocument>> SyncAllDocumentsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<LegalDocument>>(Array.Empty<LegalDocument>());
        }

        public Task<string?> SyncDocumentAsync(Guid documentId, CancellationToken cancellationToken = default)
        {
            LastSyncedDocumentId = documentId;
            return Task.FromResult(SyncResult);
        }

        public Task<IReadOnlyList<LegalDocument>> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<LegalDocument>>(Array.Empty<LegalDocument>());
        }

        public Task<IReadOnlyList<LegalDocument>> GetActiveDocumentsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<LegalDocument>>(Array.Empty<LegalDocument>());
        }

        public Task<IReadOnlyList<DocumentVersion>> GetRequiredVersionsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<DocumentVersion>>(Array.Empty<DocumentVersion>());
        }

        public Task<DocumentVersion?> GetVersionByIdAsync(Guid versionId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<DocumentVersion?>(null);
        }
    }
}
