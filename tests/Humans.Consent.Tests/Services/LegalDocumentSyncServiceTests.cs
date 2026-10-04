using System.Text;
using AwesomeAssertions;
using Humans.Notifications.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NSubstitute;
using Humans.Base.Configuration;
using Humans.Teams.Contracts;
using Humans.Consent.Services;
using Humans.Consent.Domain;
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

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task RequiredVersionReads_SelectLatestEffectiveVersion_AndOmitFutureOnlyDocuments(bool forTeam)
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var document = await SeedDocumentAsync("Privacy");
        var futureOnly = await SeedDocumentAsync("Future agreement");
        var now = Clock.GetCurrentInstant();
        var currentId = Guid.NewGuid();
        foreach (var (documentId, id, effectiveFrom) in new[]
        {
            (document.Id, Guid.NewGuid(), now.Minus(Duration.FromDays(1))),
            (document.Id, currentId, now),
            (document.Id, Guid.NewGuid(), now.Plus(Duration.FromDays(1))),
            (futureOnly.Id, Guid.NewGuid(), now.Plus(Duration.FromDays(1)))
        })
        {
            LegalDb.DocumentVersions.Add(new DocumentVersion
            {
                Id = id,
                LegalDocumentId = documentId,
                VersionNumber = id.ToString(),
                CommitSha = id.ToString(),
                EffectiveFrom = effectiveFrom,
                CreatedAt = now,
                Content = new Dictionary<string, string>(StringComparer.Ordinal) { ["es"] = "Agreement" }
            });
        }
        await SaveAllAsync(ct);

        var versions = forTeam
            ? await _service.GetRequiredDocumentVersionsForTeamAsync(_team.Id, ct)
            : await _service.GetRequiredVersionsAsync(ct);

        versions.Should().ContainSingle().Which.Id.Should().Be(currentId);
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

    // ── CreateLegalDocumentWithInitialSyncAsync ──────────────────────────────

    [HumansFact(Timeout = 10000)]
    public async Task CreateLegalDocumentWithInitialSyncAsync_PersistsAndReturnsDocument()
    {
        StubGitHubFolder("privacy/", "es-content", "sha-1", "Initial commit");
        var request = new AdminLegalDocumentUpsertRequest(
            "Privacy Policy", _team.Id, true, true, 7, "privacy/");

        var result = await _service.CreateLegalDocumentWithInitialSyncAsync(
            request, Xunit.TestContext.Current.CancellationToken);
        var documents = await _service.GetLegalDocumentsAsync(_team.Id, Xunit.TestContext.Current.CancellationToken);

        result.Document.Name.Should().Be("Privacy Policy");
        documents.Should().ContainSingle();
        documents[0].TeamName.Should().Be("Volunteers");
        documents[0].GitHubFolderPath.Should().Be("privacy/");
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

    // ── GitHub sync — version creation & re-consent pins ─────────────────────

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task SyncDocumentAsync_LongNamesKeepNoticeWithinStorageLimit(bool update)
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        StubActiveUser();
        var prefixLength = update ? 0 : "New legal document published: ".Length;
        var name = new string('x', 198 - prefixLength) + "😀" + new string('x', 256 - (198 - prefixLength) - 2);
        var document = await SeedDocumentAsync(name, folderPath: "privacy/", currentCommitSha: "sha-1");
        if (update)
        {
            LegalDb.DocumentVersions.Add(new DocumentVersion
            {
                Id = Guid.NewGuid(),
                LegalDocumentId = document.Id,
                VersionNumber = "v1.0",
                CommitSha = "sha-1",
                Content = new Dictionary<string, string>(StringComparer.Ordinal) { ["es"] = "old" },
                EffectiveFrom = Clock.GetCurrentInstant(),
                CreatedAt = Clock.GetCurrentInstant()
            });
            await SaveAllAsync(ct);
        }
        StubGitHubFolder("privacy/", "new-es-content", "sha-2", "Updated wording");

        await _service.SyncDocumentAsync(document.Id, ct);

        var args = Notifier.ReceivedCalls().Single().GetArguments();
        var title = (string)args[3]!;
        title.EnumerateRunes().Count().Should().BeLessThanOrEqualTo(200);
        title.Should().EndWith("…").And.Contain("😀");
        var encode = () => new UTF8Encoding(false, true).GetBytes(title);
        encode.Should().NotThrow();
        ((string)args[5]!).Should().Contain(update ? "new version" : "new required legal document");
    }

    [HumansTheory]
    [Xunit.InlineData(false, false)]
    [Xunit.InlineData(true, false)]
    [Xunit.InlineData(false, true)]
    public async Task SyncDocumentAsync_LocalizesFanoutByActiveRecipientLanguage(bool update, bool deliveryFailure)
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var spanishIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var englishIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var inactiveId = Guid.NewGuid();
        var users = spanishIds.Concat(englishIds).Append(inactiveId).Select(id => UserInfo.Create(
            new User
            {
                Id = id,
                PreferredLanguage = spanishIds.Contains(id) || id == inactiveId ? "es" : id == englishIds[0] ? "en" : "unsupported",
                State = id == inactiveId ? UserState.Rejected : UserState.Active
            },
            [], [], [], UserFixtures.Profile(), [])).ToList();
        _userService.GetAllUserInfosAsync(Arg.Any<CancellationToken>()).Returns((IReadOnlyCollection<UserInfo>)users);
        var document = await SeedDocumentAsync("Privacy", folderPath: "privacy/", currentCommitSha: "sha-1");
        if (update)
        {
            LegalDb.DocumentVersions.Add(new DocumentVersion
            {
                Id = Guid.NewGuid(),
                LegalDocumentId = document.Id,
                VersionNumber = "v1.0",
                CommitSha = "sha-1",
                Content = new Dictionary<string, string>(StringComparer.Ordinal) { ["es"] = "old" },
                EffectiveFrom = Clock.GetCurrentInstant(),
                CreatedAt = Clock.GetCurrentInstant()
            });
            await SaveAllAsync(ct);
        }
        StubGitHubFolder("privacy/", "new-es-content", "sha-2", "Updated wording");
        var source = update ? NotificationSource.ReConsentRequired : NotificationSource.LegalDocumentPublished;
        if (deliveryFailure)
            Notifier.SendAsync(source, Arg.Any<NotificationClass>(), Arg.Any<NotificationPriority>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException(new IOException("First language group unavailable")), Task.CompletedTask);

        var result = await _service.SyncDocumentAsync(document.Id, ct);

        result.Should().Contain(update ? "v2.0" : "v1.0");
        var notices = Notifier.ReceivedCalls().Where(call => call.GetArguments()[0] is NotificationSource value && value == source)
            .Select(call => call.GetArguments()).ToList();
        notices.Should().HaveCount(2);
        var spanish = notices.Single(args => ((IReadOnlyList<Guid>)args[4]!).Contains(spanishIds[0]));
        ((IReadOnlyList<Guid>)spanish[4]!).Should().BeEquivalentTo(spanishIds);
        spanish[3].Should().Be(update ? "Privacy se ha actualizado — es necesario volver a dar el consentimiento" : "Nuevo documento legal publicado: Privacy");
        spanish[5].Should().Be(update ? "Se ha actualizado un documento legal obligatorio. Revísalo y firma la nueva versión." : "Se ha publicado un nuevo documento legal obligatorio. Revísalo y fírmalo.");
        spanish[6].Should().Be("/Consent");
        spanish[7].Should().Be("Revisar y consentir");
        var english = notices.Single(args => ((IReadOnlyList<Guid>)args[4]!).Contains(englishIds[0]));
        ((IReadOnlyList<Guid>)english[4]!).Should().BeEquivalentTo(englishIds);
        english[3].Should().Be(update ? "Privacy has been updated — re-consent required" : "New legal document published: Privacy");
        english[7].Should().Be("Review & Consent");
        await _userService.Received(1).GetAllUserInfosAsync(Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SyncDocumentAsync_FirstVersion_DoesNotRequireReConsent_AndEmitsPublished()
    {
        StubActiveUser();
        var document = await SeedDocumentAsync("Privacy", folderPath: "privacy/", currentCommitSha: "old-sha");
        StubGitHubFolder("privacy/", "es-content", "sha-1", "Initial commit");
        _gitHub.GetFileContentAsync("privacy/doc.md", Arg.Any<CancellationToken>())
            .Returns(new GitHubFileContent("es-content", "sha-1"), new GitHubFileContent("later-content", "sha-2"));

        var result = await _service.SyncDocumentAsync(document.Id, Xunit.TestContext.Current.CancellationToken);

        result.Should().Contain("v1.0");

        await using var verifyCtx = LegalDbFactory.CreateDbContext();
        var version = await verifyCtx.DocumentVersions
            .AsNoTracking()
            .SingleAsync(v => v.LegalDocumentId == document.Id, Xunit.TestContext.Current.CancellationToken);
        version.RequiresReConsent.Should().BeFalse(
            because: "the first synced version never invalidates prior consent — there is none");
        version.CommitSha.Should().Be("sha-1");
        version.Content["es"].Should().Be("es-content");
        await _gitHub.Received(1).GetFileContentAsync("privacy/doc.md", Arg.Any<CancellationToken>());

        await AssertFanout(NotificationSource.LegalDocumentPublished, received: true);
        await AssertFanout(NotificationSource.ReConsentRequired, received: false);
    }

    [HumansFact]
    public async Task SyncDocumentAsync_ShaChanged_CreatesReConsentVersion_AndEmitsReConsentRequired()
    {
        StubActiveUser();
        var document = await SeedDocumentAsync("Privacy", folderPath: "privacy/", currentCommitSha: "sha-1");
        LegalDb.DocumentVersions.Add(new DocumentVersion
        {
            Id = Guid.NewGuid(),
            LegalDocumentId = document.Id,
            VersionNumber = "v1.0",
            CommitSha = "sha-1",
            Content = new Dictionary<string, string>(StringComparer.Ordinal) { ["es"] = "old" },
            EffectiveFrom = Clock.GetCurrentInstant().Minus(Duration.FromDays(30)),
            RequiresReConsent = false,
            CreatedAt = Clock.GetCurrentInstant().Minus(Duration.FromDays(30))
        });
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);
        StubGitHubFolder("privacy/", "new-es-content", "sha-2", "Tightened wording");

        var result = await _service.SyncDocumentAsync(document.Id, Xunit.TestContext.Current.CancellationToken);

        result.Should().Contain("v2.0");

        await using var verifyCtx = LegalDbFactory.CreateDbContext();
        var newVersion = await verifyCtx.DocumentVersions
            .AsNoTracking()
            .SingleAsync(v => v.CommitSha == "sha-2", Xunit.TestContext.Current.CancellationToken);
        newVersion.RequiresReConsent.Should().BeTrue(
            because: "every non-initial version invalidates prior consent");

        await AssertFanout(NotificationSource.ReConsentRequired, received: true);
        await AssertFanout(NotificationSource.LegalDocumentPublished, received: false);
    }

    [HumansFact]
    public async Task SyncDocumentAsync_NoCanonicalSpanishFile_Throws_AndPersistsNoVersion()
    {
        var document = await SeedDocumentAsync("Privacy", folderPath: "privacy/");
        _gitHub.DiscoverLanguageFilesAsync("privacy/", Arg.Any<CancellationToken>())
            .Returns((IReadOnlyDictionary<string, string>)new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["en"] = "privacy/doc-en.md"
            });

        var act = () => _service.SyncDocumentAsync(document.Id, Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*No canonical Spanish file*");

        await using var verifyCtx = LegalDbFactory.CreateDbContext();
        (await verifyCtx.DocumentVersions
                .AsNoTracking()
                .AnyAsync(v => v.LegalDocumentId == document.Id, Xunit.TestContext.Current.CancellationToken))
            .Should().BeFalse(because: "a sync refused for a missing Spanish canonical must not persist a version");
    }

    private Task AssertFanout(NotificationSource source, bool received)
    {
        var call = received ? Notifier.Received(1) : Notifier.DidNotReceive();
        return call.SendAsync(
            source,
            Arg.Any<NotificationClass>(),
            Arg.Any<NotificationPriority>(),
            Arg.Any<string>(),
            Arg.Any<IReadOnlyList<Guid>>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    private void StubActiveUser()
    {
        var profile = UserFixtures.Profile(
            burnerName: "Burner", firstName: "First", lastName: "Last",
            createdAt: Clock.GetCurrentInstant());
        var user = UserInfo.Create(
            user: new User
            {
                Id = Guid.NewGuid(),
                DisplayName = profile.BurnerName,
                PreferredLanguage = "en",
                CreatedAt = profile.CreatedAt,
                State = UserFixtures.StateFor(profile),
            },
            userEmails: [],
            eventParticipations: [],
            externalLogins: [],
            profile: profile,
            communicationPreferences: []);
        _userService.GetAllUserInfosAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyCollection<UserInfo>)[user]);
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
