using System.Globalization;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using System.Security.Claims;
using Humans.Consent.Controllers;
using Humans.Consent.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Humans.AuditLog.Contracts;
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
/// <see cref="LegalDocumentSyncService"/> is the sole writer for <c>legal_documents</c>/<c>document_versions</c>,
/// owning both the admin (<see cref="IAdminLegalDocumentService"/>) and GitHub-sync
/// (<see cref="ILegalDocumentSyncManagement"/>) write surfaces, and evicts after each successful write.
/// </summary>
public sealed class LegalDocumentSyncServiceTests : ConsentTestHarness
{
    private readonly Guid _actorUserId = Guid.NewGuid();
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();
    private readonly ILegalDocumentRepository _repository;
    private readonly IGitHubLegalDocumentConnector _gitHub = Substitute.For<IGitHubLegalDocumentConnector>();
    private readonly ITeamService _teamService = Substitute.For<ITeamService>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    // The real decorator; only its eviction is exercised, so its scope factory resolves nothing.
    private readonly CachingLegalDocumentSyncService _cache;
    private readonly LegalDocumentSyncService _service;
    private readonly TeamInfo _team;

    public LegalDocumentSyncServiceTests()
        : base(Instant.FromUtc(2026, 2, 15, 18, 0))
    {
        _repository = new LegalDocumentRepository(LegalDbFactory);

        _team = SeedTeam(Guid.NewGuid(), "Volunteers");

        _teamService.GetTeamAsync(_team.Id, Arg.Any<CancellationToken>()).Returns(_team);

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
            .Returns([]);

        _cache = new CachingLegalDocumentSyncService(
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            Clock,
            NullLogger<CachingLegalDocumentSyncService>.Instance);

        _service = new LegalDocumentSyncService(
            _repository,
            _gitHub,
            Notifier,
            _teamService,
            _userService,
            _cache,
            _audit,
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
    [Xunit.InlineData(false, false)]
    [Xunit.InlineData(true, false)]
    [Xunit.InlineData(false, true)]
    [Xunit.InlineData(true, true)]
    public async Task RequiredVersionReads_SelectLatestEffectiveVersion_AndOmitFutureOnlyDocuments(
        bool forTeam, bool cached)
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

        using var provider = new ServiceCollection()
            .AddKeyedScoped<ILegalDocumentSyncManagement>(
                CachingLegalDocumentSyncService.InnerServiceKey, (_, _) => _service)
            .BuildServiceProvider();
        ILegalDocumentSyncManagement reader = cached
            ? new CachingLegalDocumentSyncService(
                provider.GetRequiredService<IServiceScopeFactory>(), Clock,
                NullLogger<CachingLegalDocumentSyncService>.Instance)
            : _service;
        var versions = forTeam
            ? await reader.GetRequiredDocumentVersionsForTeamAsync(_team.Id, ct)
            : await reader.GetRequiredVersionsAsync(ct);

        versions.Should().ContainSingle().Which.Id.Should().Be(currentId);
    }

    [HumansFact]
    public async Task RequiredDocumentCache_WarmsFromEnrichedInnerSnapshot_AndFiltersTeams()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var document = await SeedDocumentAsync("Privacy");
        var other = await SeedDocumentAsync("Other team");
        other.TeamId = Guid.NewGuid();
        var optional = await SeedDocumentAsync("Optional");
        optional.IsRequired = false;
        var inactive = await SeedDocumentAsync("Inactive");
        inactive.IsActive = false;
        var now = Clock.GetCurrentInstant();
        var version = new DocumentVersion
        {
            Id = Guid.NewGuid(),
            LegalDocumentId = document.Id,
            VersionNumber = "v1",
            CommitSha = "commit",
            EffectiveFrom = now,
            CreatedAt = now
        };
        LegalDb.DocumentVersions.Add(version);
        await SaveAllAsync(ct);

        using var provider = new ServiceCollection()
            .AddKeyedScoped<ILegalDocumentSyncManagement>(
                CachingLegalDocumentSyncService.InnerServiceKey, (_, _) => _service)
            .BuildServiceProvider();
        var cache = new CachingLegalDocumentSyncService(
            provider.GetRequiredService<IServiceScopeFactory>(), Clock,
            NullLogger<CachingLegalDocumentSyncService>.Instance);

        var all = await cache.GetActiveRequiredDocumentsForTeamsAsync(null, ct);
        all.Select(d => d.Id).Should().BeEquivalentTo([document.Id, other.Id]);
        var required = (await cache.GetActiveRequiredDocumentsForTeamsAsync([_team.Id], ct))
            .Should().ContainSingle().Subject;
        required.TeamName.Should().Be(_team.Name);
        required.Versions.Should().ContainSingle().Which.Id.Should().Be(version.Id);
        (await cache.GetActiveRequiredDocumentsForTeamsAsync([], ct)).Should().BeEmpty();
        (await cache.GetVersionByIdAsync(version.Id, ct))!.Id.Should().Be(version.Id);
        (await cache.GetRequiredVersionsAsync(ct)).Should().ContainSingle().Which.Id.Should().Be(version.Id);
    }

    [HumansFact]
    public async Task GetLegalDocumentWithVersionsAsync_CurrentVersionIsLatestAlreadyEffective()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var document = await SeedDocumentAsync("Privacy");
        var now = Clock.GetCurrentInstant();
        foreach (var (number, effectiveFrom) in new[]
        {
            ("v1", now.Minus(Duration.FromDays(2))),
            ("v2", now.Minus(Duration.FromDays(1))),
            ("v3", now.Plus(Duration.FromDays(1)))
        })
        {
            LegalDb.DocumentVersions.Add(new DocumentVersion
            {
                Id = Guid.NewGuid(),
                LegalDocumentId = document.Id,
                VersionNumber = number,
                CommitSha = number,
                EffectiveFrom = effectiveFrom,
                CreatedAt = now
            });
        }
        await SaveAllAsync(ct);

        var detail = await _service.GetLegalDocumentWithVersionsAsync(document.Id, ct);

        detail!.CurrentVersion.Should().Be("v2");
        detail.Versions.Should().HaveCount(3);
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

    [HumansTheory]
    [Xunit.InlineData("IsRequired", false)]
    [Xunit.InlineData("IsActive", false)]
    [Xunit.InlineData("GracePeriodDays", false)]
    [Xunit.InlineData(null, false)]
    [Xunit.InlineData(null, true)]
    public async Task AdminDocumentForms_require_explicit_consent_policy_before_editing(string? omitted, bool enabled)
    {
        var document = await SeedDocumentAsync("Existing document");
        document.IsRequired = false;
        document.IsActive = false;
        document.GracePeriodDays = 30;
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, TeamInfo> { [_team.Id] = _team });
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddControllers();
        using var services = registrations.BuildServiceProvider();
        var http = new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, _actorUserId.ToString())], "test"))
        };
        var controller = new AdminLegalDocumentsController(_userService, _service, _teamService,
            NullLogger<AdminLegalDocumentsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>()),
            Url = Substitute.For<IUrlHelper>()
        };
        var fields = new Dictionary<string, StringValues>(StringComparer.Ordinal)
        {
            ["Id"] = document.Id.ToString(),
            ["Name"] = "Changed document",
            ["TeamId"] = _team.Id.ToString(),
            ["IsRequired"] = enabled ? "true" : "false",
            ["IsActive"] = enabled ? "true" : "false",
            ["GracePeriodDays"] = enabled ? "1" : "365"
        };
        if (omitted is not null) fields.Remove(omitted);
        var metadata = services.GetRequiredService<IModelMetadataProvider>().GetMetadataForType(typeof(LegalDocumentEditViewModel));
        var binder = services.GetRequiredService<IModelBinderFactory>().CreateBinder(new ModelBinderFactoryContext
        {
            Metadata = metadata,
            CacheToken = typeof(LegalDocumentEditViewModel)
        });
        var context = DefaultModelBindingContext.CreateBindingContext(controller.ControllerContext,
            new FormValueProvider(BindingSource.Form, new FormCollection(fields), CultureInfo.InvariantCulture),
            metadata, bindingInfo: null, modelName: "");
        await binder.BindModelAsync(context);
        var model = (LegalDocumentEditViewModel)context.Result.Model!;
        services.GetRequiredService<IObjectModelValidator>().Validate(controller.ControllerContext, null, "", model);

        var result = await controller.EditLegalDocument(document.Id, model);

        await using var verify = await LegalDbFactory.CreateDbContextAsync(Xunit.TestContext.Current.CancellationToken);
        var stored = await verify.LegalDocuments.AsNoTracking().SingleAsync(Xunit.TestContext.Current.CancellationToken);
        if (omitted is not null)
        {
            result.Should().BeOfType<ViewResult>().Which.Model.Should().BeSameAs(model);
            controller.ModelState[omitted]!.Errors.Should().ContainSingle();
            stored.Name.Should().Be("Existing document");
            stored.IsRequired.Should().BeFalse();
            stored.IsActive.Should().BeFalse();
            stored.GracePeriodDays.Should().Be(30);
            _cache.BulkInvalidations.Should().Be(0);
            _audit.ReceivedCalls().Should().BeEmpty();
            controller.TempData.Should().BeEmpty();
        }
        else
        {
            result.Should().BeOfType<RedirectToActionResult>();
            stored.Name.Should().Be("Changed document");
            stored.IsRequired.Should().Be(enabled);
            stored.IsActive.Should().Be(enabled);
            stored.GracePeriodDays.Should().Be(enabled ? 1 : 365);
            _cache.BulkInvalidations.Should().Be(1);
        }
    }

    [HumansTheory]
    [Xunit.InlineData(false, false)]
    [Xunit.InlineData(false, true)]
    [Xunit.InlineData(true, false)]
    [Xunit.InlineData(true, true)]
    public async Task AdminDocumentWrites_reject_missing_teams_before_persistence_or_sync(bool edit, bool empty)
    {
        var document = await SeedDocumentAsync("Existing document");
        StubGitHubFolder("privacy/", "Spanish agreement", "new-sha", "Initial version");
        var targetTeamId = empty ? Guid.Empty : Guid.NewGuid();
        var model = new LegalDocumentEditViewModel
        {
            Id = edit ? document.Id : null,
            Name = "Changed document",
            TeamId = targetTeamId,
            GitHubFolderPath = "privacy/"
        };
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, _actorUserId.ToString())], "test"))
        };
        var controller = new AdminLegalDocumentsController(_userService, _service, _teamService,
            NullLogger<AdminLegalDocumentsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>()),
            Url = Substitute.For<IUrlHelper>()
        };

        var result = edit
            ? await controller.EditLegalDocument(document.Id, model)
            : await controller.CreateLegalDocument(model);

        result.Should().BeOfType<NotFoundResult>();
        await using var verify = await LegalDbFactory.CreateDbContextAsync(Xunit.TestContext.Current.CancellationToken);
        var stored = await verify.LegalDocuments.AsNoTracking().ToListAsync(Xunit.TestContext.Current.CancellationToken);
        stored.Should().ContainSingle().Which.Name.Should().Be("Existing document");
        stored[0].TeamId.Should().Be(_team.Id);
        _cache.BulkInvalidations.Should().Be(0);
        _audit.ReceivedCalls().Should().BeEmpty();
        _gitHub.ReceivedCalls().Should().BeEmpty();
        controller.TempData.Should().BeEmpty();
    }

    [HumansFact(Timeout = 10000)]
    public async Task CreateLegalDocumentWithInitialSyncAsync_PersistsAndReturnsDocument()
    {
        StubGitHubFolder("privacy/", "es-content", "sha-1", "Initial commit");
        var request = new AdminLegalDocumentUpsertRequest(
            "Privacy Policy", _team.Id, true, true, 7, "privacy/");

        var result = await _service.CreateLegalDocumentWithInitialSyncAsync(
            request, _actorUserId, Xunit.TestContext.Current.CancellationToken);
        var documents = await _service.GetLegalDocumentsAsync(_team.Id, Xunit.TestContext.Current.CancellationToken);

        result!.Document.Name.Should().Be("Privacy Policy");
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
            request, _actorUserId, Xunit.TestContext.Current.CancellationToken);

        await _audit.Received(1).LogAsync(AuditAction.LegalDocumentCreated, "LegalDocument", result!.Document.Id,
            Arg.Is<string>(text => text.Contains("grace period 7 days", StringComparison.Ordinal)), _actorUserId);
        await _audit.Received(1).LogAsync(AuditAction.LegalDocumentVersionPublished, "LegalDocument", result.Document.Id,
            Arg.Is<string>(text => text.Contains("sha-1", StringComparison.Ordinal)), _actorUserId,
            Arg.Is<Guid?>(id => id.HasValue && id.Value != Guid.Empty), "DocumentVersion");
        result.InitialSyncStatus.Should().Be(AdminLegalDocumentInitialSyncStatus.Synced);
        result.SyncMessage.Should().Contain("v1.0");

        // One invalidation for the create, one for the version add from sync.
        _cache.BulkInvalidations.Should().Be(2);
    }

    [HumansFact]
    public async Task CreateLegalDocumentWithInitialSyncAsync_SkipsSyncWithoutFolderPath()
    {
        var request = new AdminLegalDocumentUpsertRequest(
            "Privacy Policy", _team.Id, true, true, 7, null);

        var result = await _service.CreateLegalDocumentWithInitialSyncAsync(
            request, _actorUserId, Xunit.TestContext.Current.CancellationToken);

        result!.InitialSyncStatus.Should().Be(AdminLegalDocumentInitialSyncStatus.NoGitHubFolderPath);
        _cache.BulkInvalidations.Should().Be(1); // create only, no sync attempted
    }

    [HumansFact]
    public async Task CreateLegalDocumentWithInitialSyncAsync_FailedSyncKeepsCreationAudit()
    {
        var request = new AdminLegalDocumentUpsertRequest("Privacy", _team.Id, true, true, 7, "missing/");
        _gitHub.DiscoverLanguageFilesAsync("missing/", Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string>(StringComparer.Ordinal));

        var result = await _service.CreateLegalDocumentWithInitialSyncAsync(
            request, _actorUserId, Xunit.TestContext.Current.CancellationToken);

        result!.InitialSyncStatus.Should().Be(AdminLegalDocumentInitialSyncStatus.Failed);
        (await _repository.GetByIdAsync(result.Document.Id, Xunit.TestContext.Current.CancellationToken))
            .Should().NotBeNull();
        await _audit.Received(1).LogAsync(AuditAction.LegalDocumentCreated, "LegalDocument", result.Document.Id,
            Arg.Any<string>(), _actorUserId);
        _audit.ReceivedCalls().Should().ContainSingle();
    }

    [HumansFact]
    public async Task UpdateLegalDocumentAsync_UpdatesAndEvictsCache()
    {
        var document = await SeedDocumentAsync("Code of Conduct");

        var request = new AdminLegalDocumentUpsertRequest(
            "Code of Conduct v2", _team.Id, true, true, 14, document.GitHubFolderPath);

        var updated = await _service.UpdateLegalDocumentAsync(
            document.Id, request, _actorUserId, Xunit.TestContext.Current.CancellationToken);

        updated.Should().NotBeNull();
        updated.Name.Should().Be("Code of Conduct v2");
        await _audit.Received(1).LogAsync(AuditAction.LegalDocumentUpdated, "LegalDocument", document.Id,
            Arg.Is<string>(text => text.Contains("grace period 14 days", StringComparison.Ordinal)), _actorUserId);
        _cache.BulkInvalidations.Should().Be(1);
    }

    [HumansFact]
    public async Task UpdateLegalDocumentAsync_ReturnsNull_WhenNotFound_DoesNotInvalidate()
    {
        var request = new AdminLegalDocumentUpsertRequest("Missing", _team.Id, true, true, 7, null);

        var updated = await _service.UpdateLegalDocumentAsync(
            Guid.NewGuid(), request, _actorUserId, Xunit.TestContext.Current.CancellationToken);

        updated.Should().BeNull();
        _cache.BulkInvalidations.Should().Be(0);
        _audit.ReceivedCalls().Should().BeEmpty();
    }

    [HumansFact]
    public async Task ArchiveLegalDocumentAsync_ArchivesAndEvictsCache()
    {
        var document = await SeedDocumentAsync("Code of Conduct");

        var archived = await _service.ArchiveLegalDocumentAsync(
            document.Id, _actorUserId, Xunit.TestContext.Current.CancellationToken);

        archived.Should().NotBeNull();
        archived.IsActive.Should().BeFalse();
        await _audit.Received(1).LogAsync(AuditAction.LegalDocumentArchived, "LegalDocument", document.Id,
            Arg.Any<string>(), _actorUserId);
        _cache.BulkInvalidations.Should().Be(1);
    }

    [HumansFact]
    public async Task ArchiveLegalDocumentAsync_ReturnsNull_WhenNotFound_DoesNotInvalidate()
    {
        var archived = await _service.ArchiveLegalDocumentAsync(
            Guid.NewGuid(), _actorUserId, Xunit.TestContext.Current.CancellationToken);

        archived.Should().BeNull();
        _cache.BulkInvalidations.Should().Be(0);
        _audit.ReceivedCalls().Should().BeEmpty();
    }

    [HumansTheory]
    [Xunit.InlineData(0)]
    [Xunit.InlineData(2000)]
    [Xunit.InlineData(2001)]
    [Xunit.InlineData(-1)]
    [Xunit.InlineData(-2)]
    public async Task UpdateVersionSummaryAsync_ValidatesTrimmedLengthBeforePersistenceAndInvalidation(int length)
    {
        var document = await SeedDocumentAsync("Code of Conduct");
        var versionId = Guid.NewGuid();

        LegalDb.DocumentVersions.Add(new DocumentVersion
        {
            Id = versionId,
            LegalDocumentId = document.Id,
            VersionNumber = "v1",
            CommitSha = "abc123",
            ChangesSummary = "Existing summary",
            EffectiveFrom = Clock.GetCurrentInstant(),
            CreatedAt = Clock.GetCurrentInstant()
        });
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var summary = length switch
        {
            0 => "  Clarified scope  ",
            -1 => "   ",
            -2 => null,
            _ => $"  {new string('x', length)}  "
        };
        var updated = await _service.UpdateVersionSummaryAsync(
            document.Id, versionId, summary, _actorUserId, Xunit.TestContext.Current.CancellationToken);

        // Repository created its own DbContext via the factory; read the
        // refreshed value through a fresh context rather than the test's
        // change-tracker-polluted one.
        await using var verifyCtx = await LegalDbFactory.CreateDbContextAsync(Xunit.TestContext.Current.CancellationToken);
        var version = await verifyCtx.DocumentVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == versionId, Xunit.TestContext.Current.CancellationToken);

        if (length > 2000)
        {
            updated.Should().Be(VersionSummaryUpdateResult.TooLong);
            version!.ChangesSummary.Should().Be("Existing summary");
            _cache.BulkInvalidations.Should().Be(0);
            _audit.ReceivedCalls().Should().BeEmpty();
        }
        else
        {
            updated.Should().Be(VersionSummaryUpdateResult.Updated);
            version.Should().NotBeNull();
            version.ChangesSummary.Should().Be(string.IsNullOrWhiteSpace(summary) ? null : summary.Trim());
            var expectedAudit = $"Updated summary of version {versionId} to '{summary?.Trim()}'.";
            await _audit.Received(1).LogAsync(AuditAction.LegalDocumentVersionSummaryUpdated, "LegalDocument", document.Id,
                Arg.Is<string>(text => string.Equals(text, expectedAudit, StringComparison.Ordinal)),
                _actorUserId, versionId, "DocumentVersion");
            _cache.BulkInvalidations.Should().Be(1);
        }
    }

    [HumansFact]
    public async Task UpdateVersionSummaryAsync_ReturnsNotFound_WhenVersionNotFound_DoesNotInvalidate()
    {
        var document = await SeedDocumentAsync("Code of Conduct");

        var updated = await _service.UpdateVersionSummaryAsync(
            document.Id, Guid.NewGuid(), "Anything", _actorUserId, Xunit.TestContext.Current.CancellationToken);

        updated.Should().Be(VersionSummaryUpdateResult.NotFound);
        _cache.BulkInvalidations.Should().Be(0);
        _audit.ReceivedCalls().Should().BeEmpty();
    }

    [HumansFact]
    public async Task SyncLegalDocumentAsync_DelegatesToSameSyncPath()
    {
        var document = await SeedDocumentAsync("Privacy", folderPath: "privacy/", currentCommitSha: "old-sha");
        StubGitHubFolder("privacy/", "new-content", "new-sha", "Updated content");

        var result = await _service.SyncLegalDocumentAsync(
            document.Id, _actorUserId, Xunit.TestContext.Current.CancellationToken);

        await _audit.Received(1).LogAsync(AuditAction.LegalDocumentVersionPublished, "LegalDocument", document.Id,
            Arg.Is<string>(text => text.Contains("new-sha", StringComparison.Ordinal)), _actorUserId,
            Arg.Any<Guid?>(), "DocumentVersion");
        result.Should().Contain("v1.0");
        _cache.BulkInvalidations.Should().Be(1);
    }

    [HumansFact]
    public async Task SyncDocumentAsync_AlreadyCurrent_TouchesAndEvictsCache()
    {
        var document = await SeedDocumentAsync("Privacy", folderPath: "privacy/", currentCommitSha: "same-sha");
        StubGitHubFolder("privacy/", "es-content", "same-sha", "No-op commit");

        var result = await _service.SyncDocumentAsync(
            document.Id, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeNull();
        _cache.BulkInvalidations.Should().Be(1);
        _audit.ReceivedCalls().Should().BeEmpty();
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task SyncDocumentAsync_LongNamesDispatchFullNoticeCopy(bool update)
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
        args[3].Should().Be(update ? $"{name} has been updated — re-consent required" : $"New legal document published: {name}");
        args[5].Should().Be(update
            ? "A required legal document has been updated. Please review and sign the new version."
            : "A new required legal document has been published. Please review and sign it.");
    }

    [HumansFact]
    public async Task SyncDocumentAsync_NewVersion_EvictsCacheBeforeNoticeFanout()
    {
        // A recipient following the notice must find the new version, so the writer
        // evicts before it fans out, not after it returns.
        var ct = Xunit.TestContext.Current.CancellationToken;
        StubActiveUser();
        var document = await SeedDocumentAsync("Privacy", folderPath: "privacy/", currentCommitSha: "sha-1");
        StubGitHubFolder("privacy/", "new-es-content", "sha-2", "Initial");
        long? evictionsAtFanout = null;
        Notifier.When(n => n.SendAsync(Arg.Any<NotificationSource>(), Arg.Any<NotificationClass>(),
                Arg.Any<NotificationPriority>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<Guid>>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>()))
            .Do(_ => evictionsAtFanout ??= _cache.BulkInvalidations);

        await _service.SyncDocumentAsync(document.Id, ct);

        evictionsAtFanout.Should().Be(1);
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
        _userService.GetAllUserInfosAsync(Arg.Any<CancellationToken>()).Returns(users);
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
            .Returns(new GitHubFileContent("es-content", "sha-1", "commit-sha-1"), new GitHubFileContent("later-content", "sha-2", "commit-2"));

        var result = await _service.SyncDocumentAsync(document.Id, Xunit.TestContext.Current.CancellationToken);

        result.Should().Contain("v1.0");

        await using var verifyCtx = await LegalDbFactory.CreateDbContextAsync(Xunit.TestContext.Current.CancellationToken);
        var version = await verifyCtx.DocumentVersions
            .AsNoTracking()
            .SingleAsync(v => v.LegalDocumentId == document.Id, Xunit.TestContext.Current.CancellationToken);
        version.RequiresReConsent.Should().BeFalse(
            because: "the first synced version never invalidates prior consent — there is none");
        version.CommitSha.Should().Be("sha-1");
        version.Content["es"].Should().Be("es-content");
        version.ChangesSummary.Should().Be("Initial commit");
        await _audit.Received(1).LogAsync(AuditAction.LegalDocumentVersionPublished, "LegalDocument", document.Id,
            Arg.Any<string>(), nameof(LegalDocumentSyncService), version.Id, "DocumentVersion");
        await _gitHub.Received(1).GetFileContentAsync("privacy/doc.md", Arg.Any<CancellationToken>());

        await AssertFanoutAsync(NotificationSource.LegalDocumentPublished, received: true);
        await AssertFanoutAsync(NotificationSource.ReConsentRequired, received: false);
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

        await using var verifyCtx = await LegalDbFactory.CreateDbContextAsync(Xunit.TestContext.Current.CancellationToken);
        var newVersion = await verifyCtx.DocumentVersions
            .AsNoTracking()
            .SingleAsync(v => v.CommitSha == "sha-2", Xunit.TestContext.Current.CancellationToken);
        newVersion.RequiresReConsent.Should().BeTrue(
            because: "every non-initial version invalidates prior consent");

        await AssertFanoutAsync(NotificationSource.ReConsentRequired, received: true);
        await AssertFanoutAsync(NotificationSource.LegalDocumentPublished, received: false);
    }

    [HumansFact]
    public async Task SyncDocumentAsync_NoCanonicalSpanishFile_Throws_AndPersistsNoVersion()
    {
        var document = await SeedDocumentAsync("Privacy", folderPath: "privacy/");
        _gitHub.DiscoverLanguageFilesAsync("privacy/", Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["en"] = "privacy/doc-en.md"
            });

        var act = () => _service.SyncDocumentAsync(document.Id, Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*No canonical Spanish file*");

        await using var verifyCtx = await LegalDbFactory.CreateDbContextAsync(Xunit.TestContext.Current.CancellationToken);
        (await verifyCtx.DocumentVersions
                .AsNoTracking()
                .AnyAsync(v => v.LegalDocumentId == document.Id, Xunit.TestContext.Current.CancellationToken))
            .Should().BeFalse(because: "a sync refused for a missing Spanish canonical must not persist a version");
        _audit.ReceivedCalls().Should().BeEmpty();
    }

    [HumansTheory]
    [Xunit.InlineData("")]
    [Xunit.InlineData(" \r\n ")]
    public async Task SyncDocumentAsync_BlankCanonicalContent_LeavesDocumentUnchanged(string content)
    {
        var document = await SeedDocumentAsync("Privacy", folderPath: "privacy/", currentCommitSha: "old-sha");
        StubGitHubFolder("privacy/", content, "new-sha", "Empty canonical file");
        Func<Task> sync = () => _service.SyncDocumentAsync(document.Id, Xunit.TestContext.Current.CancellationToken);

        await sync.Should().ThrowAsync<InvalidOperationException>().WithMessage("*canonical Spanish content*");
        await using var verify = await LegalDbFactory.CreateDbContextAsync(Xunit.TestContext.Current.CancellationToken);
        (await verify.DocumentVersions.CountAsync(Xunit.TestContext.Current.CancellationToken)).Should().Be(0);
        (await verify.LegalDocuments.SingleAsync(d => d.Id == document.Id, Xunit.TestContext.Current.CancellationToken))
            .CurrentCommitSha.Should().Be("old-sha");
        _audit.ReceivedCalls().Should().BeEmpty();
        _cache.BulkInvalidations.Should().Be(0);
        Notifier.ReceivedCalls().Should().BeEmpty();
    }

    private Task AssertFanoutAsync(NotificationSource source, bool received)
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
            .Returns([user]);
    }

    private void StubGitHubFolder(string folderPath, string content, string sha, string commitMessage)
    {
        var canonicalPath = $"{folderPath}doc.md";
        _gitHub.DiscoverLanguageFilesAsync(folderPath, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string>(StringComparer.Ordinal) { ["es"] = canonicalPath });
        _gitHub.GetFileContentAsync(canonicalPath, Arg.Any<CancellationToken>())
            .Returns(new GitHubFileContent(content, sha, $"commit-{sha}"));
        _gitHub.GetCommitMessageAsync($"commit-{sha}", Arg.Any<CancellationToken>())
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
