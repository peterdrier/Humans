using AwesomeAssertions;
using Humans.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NSubstitute;
using Humans.Application.Configuration;
using Humans.Application.Interfaces.Caching;
using Humans.Consent.Contracts;
using Humans.Application.Interfaces.Repositories;
using Humans.Teams.Contracts;
using Humans.Consent.Services;
using Humans.Consent.Domain;
using Humans.Domain.Enums;
using Humans.Consent.Data;
using Humans.Users.Contracts;

namespace Humans.Consent.Tests.Services;

/// <summary>
/// Covers the merged <see cref="LegalDocumentSyncService"/> (nobodies-collective/Humans#751):
/// the sole writer for <c>legal_documents</c>/<c>document_versions</c>, owning both the admin
/// write surface (<see cref="IAdminLegalDocumentService"/>) and the GitHub-sync write surface
/// (<see cref="ILegalDocumentSyncService"/>). Invalidator assertions here replace the deleted
/// <c>LegalDocumentSaveChangesInterceptorTests</c> — invalidation now fires from the service
/// after each successful repository write instead of from a cross-cutting EF interceptor.
/// </summary>
public sealed class LegalDocumentSyncServiceTests : ConsentTestHarness
{
    private readonly ILegalDocumentRepository _repository;
    private readonly IGitHubLegalDocumentConnector _gitHub = Substitute.For<IGitHubLegalDocumentConnector>();
    private readonly ITeamService _teamService = Substitute.For<ITeamService>();
    private readonly IUserServiceRead _userService = Substitute.For<IUserServiceRead>();
    private readonly ILegalDocumentCacheInvalidator _invalidator = Substitute.For<ILegalDocumentCacheInvalidator>();
    private readonly LegalDocumentSyncService _service;
    private readonly TeamInfo _team;

    public LegalDocumentSyncServiceTests()
        : base(Instant.FromUtc(2026, 2, 15, 18, 0))
    {
        _repository = new LegalDocumentRepository(LegalDbFactory);

        _team = SeedTeam(Guid.NewGuid(), "Volunteers");

        // Team-name stitch: return the seed team when queried.
        _teamService
            .GetTeamsWithParentsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var ids = ci.ArgAt<IReadOnlyCollection<Guid>>(0);
                IReadOnlyDictionary<Guid, TeamInfo> map = ids.Contains(_team.Id)
                    ? new Dictionary<Guid, TeamInfo> { [_team.Id] = _team }
                    : new Dictionary<Guid, TeamInfo>();
                return Task.FromResult(map);
            });

        _userService.GetAllUserInfosAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyCollection<UserInfo>)[]);

        _service = new LegalDocumentSyncService(
            _repository,
            _gitHub,
            Notifier,
            _teamService,
            _userService,
            _invalidator,
            Options.Create(new GitHubSettings
            {
                Owner = "owner",
                Repository = "repo",
                Branch = "main"
            }),
            Clock,
            NullLogger<LegalDocumentSyncService>.Instance);
    }

    // ── NormalizeGitHubFolderPath ────────────────────────────────────────────

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

    // ── CreateLegalDocumentAsync ─────────────────────────────────────────────

    [HumansFact(Timeout = 10000)]
    public async Task CreateLegalDocumentAsync_PersistsAndReturnsDocument()
    {
        var request = new AdminLegalDocumentUpsertRequest(
            "Privacy Policy", _team.Id, true, true, 7, "privacy/");

        var document = await _service.CreateLegalDocumentAsync(request, Xunit.TestContext.Current.CancellationToken);
        var documents = await _service.GetLegalDocumentsAsync(_team.Id, Xunit.TestContext.Current.CancellationToken);

        document.Name.Should().Be("Privacy Policy");
        documents.Should().ContainSingle();
        documents[0].TeamName.Should().Be("Volunteers");
        documents[0].GitHubFolderPath.Should().Be("privacy/");
    }

    [HumansFact]
    public async Task CreateLegalDocumentAsync_InvokesInvalidator()
    {
        var request = new AdminLegalDocumentUpsertRequest(
            "Privacy Policy", _team.Id, true, true, 7, null);

        await _service.CreateLegalDocumentAsync(request, Xunit.TestContext.Current.CancellationToken);

        _invalidator.Received(1).InvalidateAll();
    }

    [HumansFact]
    public async Task CreateLegalDocumentWithInitialSyncAsync_SyncsWhenFolderPathIsPresent()
    {
        StubGitHubFolder("privacy/", "es-content", "sha-1", "Initial commit");

        var request = new AdminLegalDocumentUpsertRequest(
            "Privacy Policy", _team.Id, true, true, 7, "privacy/");

        var result = await _service.CreateLegalDocumentWithInitialSyncAsync(
            request, Xunit.TestContext.Current.CancellationToken);

        result.InitialSyncStatus.Should().Be(AdminLegalDocumentInitialSyncStatus.Synced);
        result.SyncMessage.Should().Contain("v1.0");

        // One invalidation for the create, one for the version add from sync.
        _invalidator.Received(2).InvalidateAll();
    }

    [HumansFact]
    public async Task CreateLegalDocumentWithInitialSyncAsync_SkipsSyncWithoutFolderPath()
    {
        var request = new AdminLegalDocumentUpsertRequest(
            "Privacy Policy", _team.Id, true, true, 7, null);

        var result = await _service.CreateLegalDocumentWithInitialSyncAsync(
            request, Xunit.TestContext.Current.CancellationToken);

        result.InitialSyncStatus.Should().Be(AdminLegalDocumentInitialSyncStatus.NoGitHubFolderPath);
        _invalidator.Received(1).InvalidateAll(); // create only, no sync attempted
    }

    // ── UpdateLegalDocumentAsync ─────────────────────────────────────────────

    [HumansFact]
    public async Task UpdateLegalDocumentAsync_UpdatesAndInvokesInvalidator()
    {
        var document = await SeedDocumentAsync("Code of Conduct");

        var request = new AdminLegalDocumentUpsertRequest(
            "Code of Conduct v2", _team.Id, true, true, 14, document.GitHubFolderPath);

        var updated = await _service.UpdateLegalDocumentAsync(
            document.Id, request, Xunit.TestContext.Current.CancellationToken);

        updated.Should().NotBeNull();
        updated!.Name.Should().Be("Code of Conduct v2");
        _invalidator.Received(1).InvalidateAll();
    }

    [HumansFact]
    public async Task UpdateLegalDocumentAsync_ReturnsNull_WhenNotFound_DoesNotInvalidate()
    {
        var request = new AdminLegalDocumentUpsertRequest("Missing", _team.Id, true, true, 7, null);

        var updated = await _service.UpdateLegalDocumentAsync(
            Guid.NewGuid(), request, Xunit.TestContext.Current.CancellationToken);

        updated.Should().BeNull();
        _invalidator.DidNotReceive().InvalidateAll();
    }

    // ── ArchiveLegalDocumentAsync ─────────────────────────────────────────────

    [HumansFact]
    public async Task ArchiveLegalDocumentAsync_ArchivesAndInvokesInvalidator()
    {
        var document = await SeedDocumentAsync("Code of Conduct");

        var archived = await _service.ArchiveLegalDocumentAsync(
            document.Id, Xunit.TestContext.Current.CancellationToken);

        archived.Should().NotBeNull();
        archived!.IsActive.Should().BeFalse();
        _invalidator.Received(1).InvalidateAll();
    }

    [HumansFact]
    public async Task ArchiveLegalDocumentAsync_ReturnsNull_WhenNotFound_DoesNotInvalidate()
    {
        var archived = await _service.ArchiveLegalDocumentAsync(
            Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        archived.Should().BeNull();
        _invalidator.DidNotReceive().InvalidateAll();
    }

    // ── UpdateVersionSummaryAsync ─────────────────────────────────────────────

    [HumansFact]
    public async Task UpdateVersionSummaryAsync_TrimsPersistsSummary_AndInvokesInvalidator()
    {
        var document = await SeedDocumentAsync("Code of Conduct");
        var versionId = Guid.NewGuid();

        LegalDb.DocumentVersions.Add(new DocumentVersion
        {
            Id = versionId,
            LegalDocumentId = document.Id,
            VersionNumber = "v1",
            CommitSha = "abc123",
            EffectiveFrom = Clock.GetCurrentInstant(),
            CreatedAt = Clock.GetCurrentInstant()
        });
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var updated = await _service.UpdateVersionSummaryAsync(
            document.Id, versionId, "  Clarified scope  ", Xunit.TestContext.Current.CancellationToken);

        // Repository created its own DbContext via the factory; read the
        // refreshed value through a fresh context rather than the test's
        // change-tracker-polluted one.
        await using var verifyCtx = LegalDbFactory.CreateDbContext();
        var version = await verifyCtx.DocumentVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == versionId, Xunit.TestContext.Current.CancellationToken);

        updated.Should().BeTrue();
        version.Should().NotBeNull();
        version!.ChangesSummary.Should().Be("Clarified scope");
        _invalidator.Received(1).InvalidateAll();
    }

    [HumansFact]
    public async Task UpdateVersionSummaryAsync_ReturnsFalse_WhenVersionNotFound_DoesNotInvalidate()
    {
        var document = await SeedDocumentAsync("Code of Conduct");

        var updated = await _service.UpdateVersionSummaryAsync(
            document.Id, Guid.NewGuid(), "Anything", Xunit.TestContext.Current.CancellationToken);

        updated.Should().BeFalse();
        _invalidator.DidNotReceive().InvalidateAll();
    }

    // ── SyncLegalDocumentAsync (IAdminLegalDocumentService) ──────────────────

    [HumansFact]
    public async Task SyncLegalDocumentAsync_DelegatesToSameSyncPath()
    {
        var document = await SeedDocumentAsync("Privacy", folderPath: "privacy/", currentCommitSha: "old-sha");
        StubGitHubFolder("privacy/", "new-content", "new-sha", "Updated content");

        var result = await _service.SyncLegalDocumentAsync(
            document.Id, Xunit.TestContext.Current.CancellationToken);

        result.Should().Contain("v1.0");
        _invalidator.Received(1).InvalidateAll();
    }

    // ── GitHub sync — touch-only path (no content change) ────────────────────

    [HumansFact]
    public async Task SyncDocumentAsync_AlreadyCurrent_TouchesAndInvokesInvalidator()
    {
        var document = await SeedDocumentAsync("Privacy", folderPath: "privacy/", currentCommitSha: "same-sha");
        StubGitHubFolder("privacy/", "es-content", "same-sha", "No-op commit");

        var result = await _service.SyncDocumentAsync(
            document.Id, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeNull();
        _invalidator.Received(1).InvalidateAll();
    }

    private void StubGitHubFolder(string folderPath, string content, string sha, string commitMessage)
    {
        var canonicalPath = $"{folderPath}doc.md";
        _gitHub.DiscoverLanguageFilesAsync(folderPath, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyDictionary<string, string>)new Dictionary<string, string>(StringComparer.Ordinal) { ["es"] = canonicalPath });
        _gitHub.GetFileContentAsync(canonicalPath, Arg.Any<CancellationToken>())
            .Returns(new GitHubFileContent(content, sha));
        _gitHub.GetCommitMessageAsync(sha, Arg.Any<CancellationToken>())
            .Returns(commitMessage);
    }

    private async Task<LegalDocument> SeedDocumentAsync(
        string name, string? folderPath = null, string currentCommitSha = "seed")
    {
        var document = new LegalDocument
        {
            Id = Guid.NewGuid(),
            Name = name,
            TeamId = _team.Id,
            IsRequired = true,
            IsActive = true,
            GracePeriodDays = 0,
            CurrentCommitSha = currentCommitSha,
            GitHubFolderPath = folderPath ?? $"{name.ToLowerInvariant()}/",
            CreatedAt = Clock.GetCurrentInstant(),
            LastSyncedAt = Clock.GetCurrentInstant()
        };

        LegalDb.LegalDocuments.Add(document);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);
        return document;
    }
}
