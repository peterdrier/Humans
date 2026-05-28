using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using Humans.Application.Interfaces;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using ConsentService = Humans.Application.Services.Consent.ConsentService;
using Humans.Application.Interfaces.Legal;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Domain.Enums;
using Humans.Application.Interfaces.Users;
using Humans.Infrastructure.Repositories.Consent;

namespace Humans.Application.Tests.Services;

public sealed class ConsentServiceTests : ServiceTestHarness
{
    private readonly ConsentService _service;
    private readonly IMembershipCalculator _membershipCalculator = Substitute.For<IMembershipCalculator>();
    private readonly ILegalDocumentSyncService _legalDocumentSyncService = Substitute.For<ILegalDocumentSyncService>();
    private readonly INotificationInboxService _notificationInboxService = Substitute.For<INotificationInboxService>();
    private readonly ISystemTeamSync _syncJob = Substitute.For<ISystemTeamSync>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IHumansMetrics _metrics = Substitute.For<IHumansMetrics>();

    public ConsentServiceTests()
    {
        var serviceProvider = new ServiceLocatorBuilder()
            .With(_membershipCalculator)
            .Build();

        _legalDocumentSyncService
            .GetVersionByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Db.DocumentVersions
                .Include(v => v.LegalDocument)
                .Where(v => v.Id == callInfo.ArgAt<Guid>(0))
                .Select(v => new LegalDocumentVersionSnapshot(
                    v.Id,
                    v.LegalDocumentId,
                    v.LegalDocument.Name,
                    v.LegalDocument.GracePeriodDays,
                    v.VersionNumber,
                    v.Content,
                    v.EffectiveFrom,
                    v.RequiresReConsent,
                    v.CreatedAt,
                    v.ChangesSummary))
                .FirstOrDefaultAsync());

        _legalDocumentSyncService
            .GetActiveRequiredDocumentsForTeamsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                var teamIds = callInfo.ArgAt<IReadOnlyCollection<Guid>>(0);
                if (teamIds.Count == 0)
                    return (IReadOnlyList<ActiveRequiredLegalDocumentSnapshot>)[];

                var teamNamesById = await Db.Teams
                    .AsNoTracking()
                    .Where(t => teamIds.Contains(t.Id))
                    .ToDictionaryAsync(t => t.Id, t => t.Name);

                var documents = await Db.LegalDocuments
                    .AsNoTracking()
                    .Where(d => d.IsActive && d.IsRequired && teamIds.Contains(d.TeamId))
                    .Include(d => d.Versions)
                    .ToListAsync();

                return documents.Select(d => ToActiveRequiredDocumentSnapshot(d, teamNamesById)).ToList();
            });

        var consentRepository = new ConsentRepository(DbFactory);

        // Default: no merge tombstones — chain-follow short-circuits to the
        // single-id repo path.
        _userService.GetMergedSourceIdsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<Guid>());

        // Default: requesting any user returns a UserInfo carrying an Active
        // profile with all required identity fields populated. Tests that need
        // a Stub-state (or missing) profile override this for the specific
        // userId.
        _userService.GetUserInfoAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => WrapInUserInfo(new Profile
            {
                Id = Guid.NewGuid(),
                UserId = callInfo.ArgAt<Guid>(0),
                BurnerName = "Burner",
                FirstName = "First",
                LastName = "Last",
                State = ProfileState.Active,
                CreatedAt = Clock.GetCurrentInstant(),
                UpdatedAt = Clock.GetCurrentInstant()
            }));

        _service = new ConsentService(
            consentRepository,
            _legalDocumentSyncService,
            _notificationInboxService,
            _syncJob,
            _userService,
            serviceProvider,
            _metrics,
            Clock,
            NullLogger<ConsentService>.Instance);
    }

    private static UserInfo WrapInUserInfo(Profile profile) => UserInfo.Create(
        user: new User
        {
            Id = profile.UserId,
            DisplayName = profile.BurnerName,
            PreferredLanguage = "en",
            CreatedAt = profile.CreatedAt,
            GoogleEmailStatus = GoogleEmailStatus.Unknown,
        },
        userEmails: [],
        eventParticipations: [],
        externalLogins: [],
        profile: profile,
        contactFields: [],
        profileLanguages: [],
        volunteerHistory: [],
        communicationPreferences: []);

    [HumansFact]
    public async Task SubmitConsentAsync_ValidConsent_CreatesRecord()
    {
        var userId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        SeedDocumentVersion(versionId, "Test Doc", new Dictionary<string, string>(StringComparer.Ordinal) { ["es"] = "Spanish text" });

        var result = await _service.SubmitConsentAsync(userId, versionId, true, "192.168.1.1", "TestAgent");

        result.Success.Should().BeTrue();
        var record = await Db.ConsentRecords.FirstAsync();
        record.UserId.Should().Be(userId);
        record.DocumentVersionId.Should().Be(versionId);
        record.IpAddress.Should().Be("192.168.1.1");
        record.UserAgent.Should().Be("TestAgent");
        record.ExplicitConsent.Should().BeTrue();
        record.ConsentedAt.Should().Be(Clock.GetCurrentInstant());
    }

    [HumansFact]
    public async Task SubmitConsentAsync_ComputesCorrectSha256Hash()
    {
        var userId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        SeedDocumentVersion(versionId, "Test Doc", new Dictionary<string, string>(StringComparer.Ordinal) { ["es"] = "Spanish text" });

        await _service.SubmitConsentAsync(userId, versionId, true, "127.0.0.1", "Agent");

        var record = await Db.ConsentRecords.FirstAsync();
        var expectedHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes("Spanish text"))).ToLowerInvariant();
        record.ContentHash.Should().Be(expectedHash);
    }

    [HumansFact]
    public async Task SubmitConsentAsync_AlreadyConsented_ReturnsError()
    {
        var userId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        SeedDocumentVersion(versionId, "Test Doc", new Dictionary<string, string>(StringComparer.Ordinal) { ["es"] = "text" });
        Db.ConsentRecords.Add(new ConsentRecord
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            DocumentVersionId = versionId,
            ConsentedAt = Clock.GetCurrentInstant(),
            IpAddress = "127.0.0.1",
            UserAgent = "Agent",
            ContentHash = "abc",
            ExplicitConsent = true
        });
        await Db.SaveChangesAsync();

        var result = await _service.SubmitConsentAsync(userId, versionId, true, "127.0.0.1", "Agent");

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("AlreadyConsented");
    }

    [HumansFact]
    public async Task SubmitConsentAsync_DocumentNotFound_ReturnsError()
    {
        var result = await _service.SubmitConsentAsync(Guid.NewGuid(), Guid.NewGuid(), true, "127.0.0.1", "Agent");

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("NotFound");
    }

    [HumansFact]
    public async Task SubmitConsentAsync_TruncatesLongUserAgent()
    {
        var userId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        SeedDocumentVersion(versionId, "Test Doc", new Dictionary<string, string>(StringComparer.Ordinal) { ["es"] = "text" });
        var longAgent = new string('A', 600);

        await _service.SubmitConsentAsync(userId, versionId, true, "127.0.0.1", longAgent);

        var record = await Db.ConsentRecords.FirstAsync();
        record.UserAgent.Should().HaveLength(500);
    }

    // Threshold check (formerly SubmitConsentAsync_CallsSetConsentCheckPending)
    // moved out of ConsentService entirely — it's a director method on
    // IOnboardingService now, invoked by controllers as a peer call after
    // SubmitConsentAsync. ConsentService has no dep on Onboarding or Profile.

    [HumansFact]
    public async Task SubmitConsentAsync_CallsSyncJobs()
    {
        var userId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        SeedDocumentVersion(versionId, "Test Doc", new Dictionary<string, string>(StringComparer.Ordinal) { ["es"] = "text" });

        await _service.SubmitConsentAsync(userId, versionId, true, "127.0.0.1", "Agent");

        await _syncJob.Received().SyncMembershipForUserAsync(
            userId, SystemTeamType.Volunteers, Arg.Any<CancellationToken>());
        await _syncJob.Received().SyncMembershipForUserAsync(
            userId, SystemTeamType.Coordinators, Arg.Any<CancellationToken>());
    }

    private static ActiveRequiredLegalDocumentSnapshot ToActiveRequiredDocumentSnapshot(
        LegalDocument document,
        IReadOnlyDictionary<Guid, string> teamNamesById) =>
        new(
            document.Id,
            document.Name,
            document.TeamId,
            teamNamesById.GetValueOrDefault(document.TeamId, string.Empty),
            document.LastSyncedAt,
            document.Versions.Select(v => new LegalDocumentVersionSnapshot(
                v.Id,
                v.LegalDocumentId,
                document.Name,
                document.GracePeriodDays,
                v.VersionNumber,
                v.Content,
                v.EffectiveFrom,
                v.RequiresReConsent,
                v.CreatedAt,
                v.ChangesSummary)).ToList());

    [HumansFact]
    public async Task SubmitConsentAsync_RecordsMetric()
    {
        var userId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        SeedDocumentVersion(versionId, "Test Doc", new Dictionary<string, string>(StringComparer.Ordinal) { ["es"] = "text" });

        await _service.SubmitConsentAsync(userId, versionId, true, "127.0.0.1", "Agent");

        _metrics.Received().RecordConsentGiven();
    }

    [HumansFact]
    public async Task SubmitConsentAsync_ReturnsDocumentName()
    {
        var userId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        SeedDocumentVersion(versionId, "Privacy Policy", new Dictionary<string, string>(StringComparer.Ordinal) { ["es"] = "text" });

        var result = await _service.SubmitConsentAsync(userId, versionId, true, "127.0.0.1", "Agent");

        result.DocumentName.Should().Be("Privacy Policy");
    }

    [HumansFact]
    public async Task SubmitConsentAsync_StubProfile_ReturnsStubProfileErrorAndWritesNoRecord()
    {
        var userId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        SeedDocumentVersion(versionId, "Privacy Policy", new Dictionary<string, string>(StringComparer.Ordinal) { ["es"] = "text" });

        // Stub profile = required identity fields blank.
        var stubProfile = new Profile
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            BurnerName = "",
            FirstName = "",
            LastName = "",
            State = ProfileState.Stub,
            CreatedAt = Clock.GetCurrentInstant(),
            UpdatedAt = Clock.GetCurrentInstant()
        };
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(WrapInUserInfo(stubProfile));

        var result = await _service.SubmitConsentAsync(userId, versionId, true, "127.0.0.1", "Agent");

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("StubProfile");
        (await Db.ConsentRecords.CountAsync()).Should().Be(0);
    }

    [HumansFact]
    public async Task SubmitConsentAsync_ActiveProfile_AllowsWrite()
    {
        var userId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        SeedDocumentVersion(versionId, "Privacy Policy", new Dictionary<string, string>(StringComparer.Ordinal) { ["es"] = "text" });

        var activeProfile = new Profile
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            BurnerName = "Burner",
            FirstName = "First",
            LastName = "Last",
            State = ProfileState.Active,
            CreatedAt = Clock.GetCurrentInstant(),
            UpdatedAt = Clock.GetCurrentInstant()
        };
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(WrapInUserInfo(activeProfile));

        var result = await _service.SubmitConsentAsync(userId, versionId, true, "127.0.0.1", "Agent");

        result.Success.Should().BeTrue();
        (await Db.ConsentRecords.CountAsync()).Should().Be(1);
    }

    // --- GetConsentDashboardAsync ---

    [HumansFact]
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
        await Db.SaveChangesAsync();

        var (groups, _) = await _service.GetConsentDashboardAsync(userId);

        groups.Should().HaveCount(2);
    }

    [HumansFact]
    public async Task GetConsentDashboardAsync_OnlyIncludesActiveRequiredDocuments()
    {
        var userId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        _membershipCalculator.GetRequiredTeamIdsForUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { teamId });

        SeedTeam(teamId, "Team");
        SeedDocument(teamId, "Active Doc");
        SeedDocument(teamId, "Inactive Doc", isActive: false);
        await Db.SaveChangesAsync();

        var (groups, _) = await _service.GetConsentDashboardAsync(userId);

        groups.Should().HaveCount(1);
        groups[0].Documents.Should().HaveCount(1);
    }

    [HumansFact]
    public async Task GetConsentDashboardAsync_SelectsCurrentVersion()
    {
        var userId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var now = Clock.GetCurrentInstant();
        _membershipCalculator.GetRequiredTeamIdsForUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { teamId });

        SeedTeam(teamId, "Team");
        var docId = Guid.NewGuid();
        var olderVersionId = Guid.NewGuid();
        var newerVersionId = Guid.NewGuid();
        Db.LegalDocuments.Add(new LegalDocument
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
        Db.DocumentVersions.Add(new DocumentVersion
        {
            Id = olderVersionId,
            LegalDocumentId = docId,
            VersionNumber = "v1",
            CommitSha = "old",
            EffectiveFrom = now - Duration.FromDays(30),
            Content = new Dictionary<string, string>(StringComparer.Ordinal) { ["es"] = "old" },
            CreatedAt = now
        });
        Db.DocumentVersions.Add(new DocumentVersion
        {
            Id = newerVersionId,
            LegalDocumentId = docId,
            VersionNumber = "v2",
            CommitSha = "new",
            EffectiveFrom = now - Duration.FromDays(1),
            Content = new Dictionary<string, string>(StringComparer.Ordinal) { ["es"] = "new" },
            CreatedAt = now
        });
        await Db.SaveChangesAsync();

        var (groups, _) = await _service.GetConsentDashboardAsync(userId);

        groups.Should().HaveCount(1);
        groups[0].Documents.Should().HaveCount(1);
        groups[0].Documents[0].DocumentVersionId.Should().Be(newerVersionId);
    }

    [HumansFact]
    public async Task GetConsentDashboardAsync_PairsVersionWithConsent()
    {
        var userId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        _membershipCalculator.GetRequiredTeamIdsForUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { teamId });

        SeedTeam(teamId, "Team");
        var versionId = SeedDocument(teamId, "Doc");
        SeedConsentRecord(userId, versionId);
        await Db.SaveChangesAsync();

        var (groups, _) = await _service.GetConsentDashboardAsync(userId);

        groups.Should().HaveCount(1);
        groups[0].Documents[0].HasConsented.Should().BeTrue();
    }

    [HumansFact]
    public async Task GetConsentDashboardAsync_NullConsentWhenNotSigned()
    {
        var userId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        _membershipCalculator.GetRequiredTeamIdsForUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { teamId });

        SeedTeam(teamId, "Team");
        SeedDocument(teamId, "Doc");
        await Db.SaveChangesAsync();

        var (groups, _) = await _service.GetConsentDashboardAsync(userId);

        groups.Should().HaveCount(1);
        groups[0].Documents[0].HasConsented.Should().BeFalse();
    }

    [HumansFact]
    public async Task GetConsentDashboardAsync_ReturnsHistory()
    {
        var userId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var now = Clock.GetCurrentInstant();
        _membershipCalculator.GetRequiredTeamIdsForUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { teamId });

        SeedTeam(teamId, "Team");
        var v1 = SeedDocument(teamId, "Doc A");
        var v2 = SeedDocument(teamId, "Doc B");
        SeedConsentRecord(userId, v1, now - Duration.FromHours(2));
        SeedConsentRecord(userId, v2, now - Duration.FromHours(1));
        await Db.SaveChangesAsync();

        var (_, history) = await _service.GetConsentDashboardAsync(userId);

        history.Should().HaveCount(2);
        history[0].ConsentedAt.Should().BeGreaterThan(history[1].ConsentedAt);
    }

    [HumansFact]
    public async Task GetConsentDashboardAsync_ExcludesFutureVersions()
    {
        var userId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var now = Clock.GetCurrentInstant();
        _membershipCalculator.GetRequiredTeamIdsForUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { teamId });

        SeedTeam(teamId, "Team");
        SeedDocument(teamId, "Future Doc", effectiveFrom: now + Duration.FromDays(30));
        await Db.SaveChangesAsync();

        var (groups, _) = await _service.GetConsentDashboardAsync(userId);

        groups.Should().HaveCount(1);
        groups[0].Documents.Should().BeEmpty();
    }

    [HumansFact]
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

    [HumansFact]
    public async Task GetConsentReviewDetailAsync_ReturnsVersionWithDocumentAndConsent()
    {
        var userId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        SeedDocumentVersion(versionId, "Test Doc", new Dictionary<string, string>(StringComparer.Ordinal) { ["es"] = "text" });
        SeedConsentRecord(userId, versionId);
        var profile = new Profile
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            BurnerName = "Test",
            FirstName = "Jane",
            LastName = "Doe",
            CreatedAt = Clock.GetCurrentInstant(),
            UpdatedAt = Clock.GetCurrentInstant()
        };
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(WrapInUserInfo(profile));
        await Db.SaveChangesAsync();

        var detail = await _service.GetConsentReviewDetailAsync(versionId, userId);

        detail.Should().NotBeNull();
        detail.DocumentName.Should().Be("Test Doc");
        detail.HasAlreadyConsented.Should().BeTrue();
        detail.UserFullName.Should().Be("Jane Doe");
    }

    [HumansFact]
    public async Task GetConsentReviewDetailAsync_NullConsentWhenNotSigned()
    {
        var userId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        SeedDocumentVersion(versionId, "Test Doc", new Dictionary<string, string>(StringComparer.Ordinal) { ["es"] = "text" });
        var profile = new Profile
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            BurnerName = "Test",
            FirstName = "Jane",
            LastName = "Doe",
            CreatedAt = Clock.GetCurrentInstant(),
            UpdatedAt = Clock.GetCurrentInstant()
        };
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(WrapInUserInfo(profile));
        await Db.SaveChangesAsync();

        var detail = await _service.GetConsentReviewDetailAsync(versionId, userId);

        detail.Should().NotBeNull();
        detail.HasAlreadyConsented.Should().BeFalse();
        detail.UserFullName.Should().Be("Jane Doe");
    }

    [HumansFact]
    public async Task GetConsentReviewDetailAsync_VersionNotFound_ReturnsAllNulls()
    {
        var detail = await _service.GetConsentReviewDetailAsync(Guid.NewGuid(), Guid.NewGuid());

        detail.Should().BeNull();
    }

    [HumansFact]
    public async Task GetConsentReviewDetailAsync_NullFullNameWhenNoProfile()
    {
        var userId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        SeedDocumentVersion(versionId, "Test Doc", new Dictionary<string, string>(StringComparer.Ordinal) { ["es"] = "text" });
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns((UserInfo?)null);

        var detail = await _service.GetConsentReviewDetailAsync(versionId, userId);

        detail.Should().NotBeNull();
        detail.HasAlreadyConsented.Should().BeFalse();
        detail.UserFullName.Should().BeNull();
    }

    [HumansFact]
    public async Task GetConsentMapForUsersAsync_InputContainsBothSourceAndTarget_DoesNotThrow()
    {
        // Regression for peterdrier#382 finding 3175264064 (Codex P1):
        // when input list contains both a source tombstone id and its merge
        // target, the chain-follow path used to append the source id again
        // without dedup. The downstream repo's `userIds.ToDictionary(id =>
        // id, ...)` then threw ArgumentException on the duplicate key.
        var sourceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var unrelatedId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        SeedDocumentVersion(versionId, "Test Doc", new Dictionary<string, string>(StringComparer.Ordinal) { ["es"] = "text" });
        SeedConsentRecord(sourceId, versionId);
        await Db.SaveChangesAsync();

        // Target's chain-follow set includes the source.
        _userService.GetMergedSourceIdsAsync(targetId, Arg.Any<CancellationToken>())
            .Returns(new HashSet<Guid> { sourceId });
        // Source tombstone has no further sources.
        _userService.GetMergedSourceIdsAsync(sourceId, Arg.Any<CancellationToken>())
            .Returns(new HashSet<Guid>());
        _userService.GetMergedSourceIdsAsync(unrelatedId, Arg.Any<CancellationToken>())
            .Returns(new HashSet<Guid>());

        // Input contains both source and target — duplicate-id risk path.
        var result = await _service.GetConsentMapForUsersAsync([sourceId, targetId, unrelatedId]);

        result.Should().ContainKey(targetId);
        result[targetId].Should().Contain(versionId, "target's chain-follow includes the source's explicit consent");
        result.Should().ContainKey(sourceId);
        result.Should().ContainKey(unrelatedId);
    }

    // --- Helpers ---

    private Guid SeedDocument(Guid teamId, string name, bool isActive = true, bool isRequired = true, Instant? effectiveFrom = null)
    {
        var now = Clock.GetCurrentInstant();
        var docId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        Db.LegalDocuments.Add(new LegalDocument
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
        Db.DocumentVersions.Add(new DocumentVersion
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
        Db.ConsentRecords.Add(new ConsentRecord
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            DocumentVersionId = versionId,
            ExplicitConsent = true,
            ConsentedAt = consentedAt ?? Clock.GetCurrentInstant(),
            IpAddress = "127.0.0.1",
            UserAgent = "test",
            ContentHash = "testhash"
        });
    }

    private void SeedDocumentVersion(Guid versionId, string documentName, Dictionary<string, string> content)
    {
        var teamId = Guid.NewGuid();
        Db.Teams.Add(new Team
        {
            Id = teamId,
            Name = "Volunteers",
            Slug = "volunteers",
            IsActive = true,
            CreatedAt = Clock.GetCurrentInstant(),
            UpdatedAt = Clock.GetCurrentInstant()
        });
        var docId = Guid.NewGuid();
        Db.LegalDocuments.Add(new LegalDocument
        {
            Id = docId,
            Name = documentName,
            TeamId = teamId,
            IsRequired = true,
            IsActive = true,
            CurrentCommitSha = "abc123",
            CreatedAt = Clock.GetCurrentInstant(),
            LastSyncedAt = Clock.GetCurrentInstant()
        });
        Db.DocumentVersions.Add(new DocumentVersion
        {
            Id = versionId,
            LegalDocumentId = docId,
            VersionNumber = "1.0",
            CommitSha = "abc123",
            Content = content,
            EffectiveFrom = Clock.GetCurrentInstant() - Duration.FromDays(1),
            CreatedAt = Clock.GetCurrentInstant()
        });
        Db.SaveChanges();
    }
}
