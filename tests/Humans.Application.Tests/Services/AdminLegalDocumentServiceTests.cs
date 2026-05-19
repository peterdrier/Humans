using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using NSubstitute;
using Humans.Application.Configuration;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.Legal;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Services.Legal;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Repositories.Legal;

namespace Humans.Application.Tests.Services;

public sealed class AdminLegalDocumentServiceTests : ServiceTestHarness
{
    private readonly ILegalDocumentRepository _repository;
    private readonly FakeLegalDocumentSyncService _syncService;
    private readonly ITeamService _teamService = Substitute.For<ITeamService>();
    private readonly AdminLegalDocumentService _service;
    private readonly Team _team;

    public AdminLegalDocumentServiceTests()
        : base(Instant.FromUtc(2026, 2, 15, 18, 0))
    {
        _repository = new LegalDocumentRepository(DbFactory);
        _syncService = new FakeLegalDocumentSyncService();

        _team = new Team
        {
            Id = Guid.NewGuid(),
            Name = "Volunteers",
            Slug = "volunteers",
            IsActive = true,
            SystemTeamType = SystemTeamType.None,
            CreatedAt = Clock.GetCurrentInstant(),
            UpdatedAt = Clock.GetCurrentInstant()
        };

        Db.Teams.Add(_team);
        Db.SaveChanges();

        // Team-name stitch: return the seed team when queried.
        _teamService
            .GetByIdsWithParentsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var ids = ci.ArgAt<IReadOnlyCollection<Guid>>(0);
                IReadOnlyDictionary<Guid, Team> map = ids.Contains(_team.Id)
                    ? new Dictionary<Guid, Team> { [_team.Id] = _team }
                    : new Dictionary<Guid, Team>();
                return Task.FromResult(map);
            });

        _service = new AdminLegalDocumentService(
            _repository,
            _syncService,
            _teamService,
            Options.Create(new GitHubSettings
            {
                Owner = "owner",
                Repository = "repo",
                Branch = "main"
            }),
            Clock);
    }

    [HumansFact]
    public void NormalizeGitHubFolderPath_PlainPath_ReturnsNormalizedFolder()
    {
        var result = _service.NormalizeGitHubFolderPath("Volunteer");

        result.IsValid.Should().BeTrue();
        result.NormalizedFolderPath.Should().Be("Volunteer/");
        result.ErrorMessage.Should().BeNull();
    }

    [HumansFact]
    public void NormalizeGitHubFolderPath_WrongRepository_ReturnsValidationError()
    {
        var result = _service.NormalizeGitHubFolderPath("https://github.com/another/repo/tree/main/Volunteer");

        result.IsValid.Should().BeFalse();
        result.NormalizedFolderPath.Should().BeNull();
        result.ErrorMessage.Should().Contain("configured repository is owner/repo");
    }

    [HumansFact(Timeout = 10000)]
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
        documents[0].TeamName.Should().Be("Volunteers");
        documents[0].GitHubFolderPath.Should().Be("privacy/");
    }

    [HumansFact]
    public async Task CreateLegalDocumentWithInitialSyncAsync_SyncsWhenFolderPathIsPresent()
    {
        _syncService.SyncResult = "updated 1 file";
        var request = new AdminLegalDocumentUpsertRequest(
            "Privacy Policy",
            _team.Id,
            true,
            true,
            7,
            "privacy/");

        var result = await _service.CreateLegalDocumentWithInitialSyncAsync(request);

        result.InitialSyncStatus.Should().Be(AdminLegalDocumentInitialSyncStatus.Synced);
        result.SyncMessage.Should().Be("updated 1 file");
        _syncService.LastSyncedDocumentId.Should().Be(result.Document.Id);
    }

    [HumansFact]
    public async Task CreateLegalDocumentWithInitialSyncAsync_SkipsSyncWithoutFolderPath()
    {
        var request = new AdminLegalDocumentUpsertRequest(
            "Privacy Policy",
            _team.Id,
            true,
            true,
            7,
            null);

        var result = await _service.CreateLegalDocumentWithInitialSyncAsync(request);

        result.InitialSyncStatus.Should().Be(AdminLegalDocumentInitialSyncStatus.NoGitHubFolderPath);
        _syncService.LastSyncedDocumentId.Should().BeNull();
    }

    [HumansFact]
    public async Task UpdateVersionSummaryAsync_TrimsAndPersistsSummary()
    {
        var document = await SeedDocumentAsync("Code of Conduct");
        var versionId = Guid.NewGuid();

        Db.DocumentVersions.Add(new DocumentVersion
        {
            Id = versionId,
            LegalDocumentId = document.Id,
            VersionNumber = "v1",
            CommitSha = "abc123",
            EffectiveFrom = Clock.GetCurrentInstant(),
            CreatedAt = Clock.GetCurrentInstant()
        });
        await Db.SaveChangesAsync();

        var updated = await _service.UpdateVersionSummaryAsync(document.Id, versionId, "  Clarified scope  ");

        // Repository created its own DbContext via the factory; read the
        // refreshed value through a fresh context rather than the test's
        // change-tracker-polluted one.
        await using var verifyCtx = DbFactory.CreateDbContext();
        var version = await verifyCtx.DocumentVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == versionId);

        updated.Should().BeTrue();
        version.Should().NotBeNull();
        version.ChangesSummary.Should().Be("Clarified scope");
    }

    [HumansFact]
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
            CreatedAt = Clock.GetCurrentInstant(),
            LastSyncedAt = Clock.GetCurrentInstant()
        };

        Db.LegalDocuments.Add(document);
        await Db.SaveChangesAsync();
        return document;
    }

    private sealed class FakeLegalDocumentSyncService : ILegalDocumentSyncService
    {
        public Guid? LastSyncedDocumentId { get; private set; }
        public string? SyncResult { get; set; }

        public Task<IReadOnlyList<LegalDocument>> SyncAllDocumentsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<LegalDocument>>([]);

        public Task<string?> SyncDocumentAsync(Guid documentId, CancellationToken cancellationToken = default)
        {
            LastSyncedDocumentId = documentId;
            return Task.FromResult(SyncResult);
        }

        public Task<IReadOnlyList<LegalDocument>> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<LegalDocument>>([]);

        public Task<IReadOnlyList<LegalDocumentSnapshot>> GetActiveDocumentsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<LegalDocumentSnapshot>>([]);

        public Task<IReadOnlyList<RequiredDocumentVersionSnapshot>> GetRequiredVersionsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RequiredDocumentVersionSnapshot>>([]);

        public Task<LegalDocumentVersionSnapshot?> GetVersionByIdAsync(Guid versionId, CancellationToken cancellationToken = default)
            => Task.FromResult<LegalDocumentVersionSnapshot?>(null);

        public Task<IReadOnlyList<RequiredDocumentVersionSnapshot>> GetRequiredDocumentVersionsForTeamAsync(Guid teamId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<RequiredDocumentVersionSnapshot>>([]);
        }

        public Task<IReadOnlyList<ActiveRequiredLegalDocumentSnapshot>> GetActiveRequiredDocumentsForTeamsAsync(
            IReadOnlyCollection<Guid> teamIds, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<ActiveRequiredLegalDocumentSnapshot>>([]);
        }

        public Task<int> GetActiveRequiredCountAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(0);
    }
}
