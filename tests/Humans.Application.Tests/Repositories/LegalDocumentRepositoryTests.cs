using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Testing;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Xunit;
using Humans.Infrastructure.Repositories.Legal;

namespace Humans.Application.Tests.Repositories;

public sealed class LegalDocumentRepositoryTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly LegalDocumentRepository _repo;
    private readonly Team _team;

    public LegalDocumentRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new HumansDbContext(options);
        _clock = new FakeClock(Instant.FromUtc(2026, 3, 1, 12, 0));
        _repo = new LegalDocumentRepository(new TestDbContextFactory(options));

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

    private async Task<LegalDocument> SeedDocumentAsync(
        string name,
        bool isActive = true,
        bool isRequired = true)
    {
        var document = new LegalDocument
        {
            Id = Guid.NewGuid(),
            Name = name,
            TeamId = _team.Id,
            IsRequired = isRequired,
            IsActive = isActive,
            GracePeriodDays = 7,
            CurrentCommitSha = "seed",
            GitHubFolderPath = $"{name.ToLowerInvariant()}/",
            CreatedAt = _clock.GetCurrentInstant(),
            LastSyncedAt = _clock.GetCurrentInstant()
        };
        _dbContext.LegalDocuments.Add(document);
        await _dbContext.SaveChangesAsync();
        return document;
    }

    private async Task<DocumentVersion> SeedVersionAsync(
        Guid documentId,
        string versionNumber,
        Instant effectiveFrom)
    {
        var version = new DocumentVersion
        {
            Id = Guid.NewGuid(),
            LegalDocumentId = documentId,
            VersionNumber = versionNumber,
            CommitSha = Guid.NewGuid().ToString("N"),
            Content = new Dictionary<string, string>(StringComparer.Ordinal) { ["es"] = "x" },
            EffectiveFrom = effectiveFrom,
            CreatedAt = _clock.GetCurrentInstant()
        };
        _dbContext.DocumentVersions.Add(version);
        await _dbContext.SaveChangesAsync();
        return version;
    }

    // ── Reads ────────────────────────────────────────────────────────────────

    [HumansFact]
    public async Task GetByIdAsync_ReturnsNull_WhenMissing()
    {
        var result = await _repo.GetByIdAsync(Guid.NewGuid());
        result.Should().BeNull();
    }

    [HumansFact]
    public async Task GetByIdAsync_IncludesVersions()
    {
        var doc = await SeedDocumentAsync("Privacy");
        await SeedVersionAsync(doc.Id, "v1", _clock.GetCurrentInstant());

        var result = await _repo.GetByIdAsync(doc.Id);

        result.Should().NotBeNull();
        result!.Versions.Should().ContainSingle();
    }

    [HumansFact]
    public async Task GetDocumentsAsync_FiltersByTeam()
    {
        await SeedDocumentAsync("Privacy");
        await SeedDocumentAsync("CoC");

        // Different team
        var otherTeamId = Guid.NewGuid();
        _dbContext.Teams.Add(new Team
        {
            Id = otherTeamId,
            Name = "Other",
            Slug = "other",
            IsActive = true,
            SystemTeamType = SystemTeamType.None,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        });
        _dbContext.LegalDocuments.Add(new LegalDocument
        {
            Id = Guid.NewGuid(),
            Name = "Other doc",
            TeamId = otherTeamId,
            IsRequired = true,
            IsActive = true,
            GracePeriodDays = 0,
            CurrentCommitSha = "x",
            CreatedAt = _clock.GetCurrentInstant()
        });
        await _dbContext.SaveChangesAsync();

        var mine = await _repo.GetDocumentsAsync(_team.Id);
        var all = await _repo.GetDocumentsAsync(null);

        mine.Should().HaveCount(2);
        all.Should().HaveCount(3);
    }

    [HumansFact]
    public async Task GetActiveRequiredDocumentsForTeamAsync_ExcludesInactive()
    {
        await SeedDocumentAsync("Active", isActive: true, isRequired: true);
        await SeedDocumentAsync("Archived", isActive: false, isRequired: true);
        await SeedDocumentAsync("Optional", isActive: true, isRequired: false);

        var result = await _repo.GetActiveRequiredDocumentsForTeamAsync(_team.Id);

        result.Should().ContainSingle()
            .Which.Name.Should().Be("Active");
    }

    [HumansFact]
    public async Task GetVersionByIdAsync_IncludesParentDocument()
    {
        var doc = await SeedDocumentAsync("Privacy");
        var version = await SeedVersionAsync(doc.Id, "v1", _clock.GetCurrentInstant());

        var result = await _repo.GetVersionByIdAsync(version.Id);

        result.Should().NotBeNull();
        result!.LegalDocument.Should().NotBeNull();
        result.LegalDocument.Name.Should().Be("Privacy");
    }

    // ── Writes ───────────────────────────────────────────────────────────────

    [HumansFact]
    public async Task AddAsync_PersistsNewDocument()
    {
        var doc = new LegalDocument
        {
            Id = Guid.NewGuid(),
            Name = "New",
            TeamId = _team.Id,
            IsRequired = true,
            IsActive = true,
            GracePeriodDays = 5,
            CurrentCommitSha = string.Empty,
            CreatedAt = _clock.GetCurrentInstant()
        };

        var saved = await _repo.AddAsync(doc);
        saved.Id.Should().Be(doc.Id);

        // Verify via a fresh context so we don't observe the test's tracker.
        var dbCount = await _dbContext.LegalDocuments.CountAsync(d => d.Id == doc.Id);
        dbCount.Should().Be(1);
    }

    [HumansFact]
    public async Task UpdateAsync_ReturnsFalse_WhenMissing()
    {
        var updated = await _repo.UpdateAsync(
            Guid.NewGuid(),
            "x", _team.Id, true, true, 1, null);

        updated.Should().BeFalse();
    }

    [HumansFact]
    public async Task ArchiveAsync_SetsIsActiveFalse()
    {
        var doc = await SeedDocumentAsync("Privacy", isActive: true);

        var result = await _repo.ArchiveAsync(doc.Id);

        result.Should().NotBeNull();
        result!.IsActive.Should().BeFalse();
    }

    [HumansFact]
    public async Task AddVersionAsync_AddsVersionAndUpdatesDocument()
    {
        var doc = await SeedDocumentAsync("Privacy");
        var now = _clock.GetCurrentInstant();
        var newVersion = new DocumentVersion
        {
            Id = Guid.NewGuid(),
            LegalDocumentId = doc.Id,
            VersionNumber = "v1",
            CommitSha = "newsha",
            Content = new Dictionary<string, string>(StringComparer.Ordinal) { ["es"] = "x" },
            EffectiveFrom = now,
            CreatedAt = now
        };

        var ok = await _repo.AddVersionAsync(doc.Id, newVersion, "newsha", now);
        ok.Should().BeTrue();

        // Fresh read confirms parent fields were updated atomically.
        var reloaded = await _repo.GetByIdAsync(doc.Id);
        reloaded!.CurrentCommitSha.Should().Be("newsha");
        reloaded.Versions.Should().ContainSingle();
    }

    [HumansFact]
    public async Task UpdateVersionSummaryAsync_TrimsAndPersists()
    {
        var doc = await SeedDocumentAsync("Privacy");
        var version = await SeedVersionAsync(doc.Id, "v1", _clock.GetCurrentInstant());

        var updated = await _repo.UpdateVersionSummaryAsync(doc.Id, version.Id, "  trimmed  ");
        updated.Should().BeTrue();

        var reloaded = await _repo.GetVersionByIdAsync(version.Id);
        reloaded!.ChangesSummary.Should().Be("trimmed");
    }

    [HumansFact]
    public async Task UpdateVersionSummaryAsync_ReturnsFalse_WhenVersionBelongsToAnotherDocument()
    {
        var docA = await SeedDocumentAsync("A");
        var docB = await SeedDocumentAsync("B");
        var versionOfA = await SeedVersionAsync(docA.Id, "v1", _clock.GetCurrentInstant());

        var updated = await _repo.UpdateVersionSummaryAsync(docB.Id, versionOfA.Id, "summary");

        updated.Should().BeFalse();
    }
}
