using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Humans.Application.Interfaces;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using ConsentService = Humans.Application.Services.Consent.ConsentService;
using Humans.Application.Interfaces.Legal;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Domain.Enums;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Users;
using Humans.Infrastructure.Repositories.Consent;

namespace Humans.Application.Tests.Services;

public class ConsentServiceTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly ConsentService _service;
    private readonly IMembershipCalculator _membershipCalculator = Substitute.For<IMembershipCalculator>();
    private readonly ILegalDocumentSyncService _legalDocumentSyncService = Substitute.For<ILegalDocumentSyncService>();
    private readonly INotificationInboxService _notificationInboxService = Substitute.For<INotificationInboxService>();
    private readonly ISystemTeamSync _syncJob = Substitute.For<ISystemTeamSync>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IShiftSignupService _shiftSignupService = Substitute.For<IShiftSignupService>();
    private readonly IHumansMetrics _metrics = Substitute.For<IHumansMetrics>();

    public ConsentServiceTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new HumansDbContext(options);
        _clock = new FakeClock(Instant.FromUtc(2026, 3, 1, 12, 0));

        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IMembershipCalculator)).Returns(_membershipCalculator);
        serviceProvider.GetService(typeof(IShiftSignupService)).Returns(_shiftSignupService);

        _legalDocumentSyncService
            .GetVersionByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => _dbContext.DocumentVersions
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

#pragma warning disable CS0618 // Test stub uses LegalDocument.Team to populate the snapshot's TeamName; production stitches via ITeamService.
                var documents = await _dbContext.LegalDocuments
                    .AsNoTracking()
                    .Where(d => d.IsActive && d.IsRequired && teamIds.Contains(d.TeamId))
                    .Include(d => d.Team)
                    .Include(d => d.Versions)
                    .ToListAsync();

                return documents.Select(ToActiveRequiredDocumentSnapshot).ToList();
#pragma warning restore CS0618
            });

        var factory = new TestDbContextFactory(options);
        var consentRepository = new ConsentRepository(factory);

        // Default: no merge tombstones — chain-follow short-circuits to the
        // single-id repo path.
        _userService.GetMergedSourceIdsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlySet<Guid>)new HashSet<Guid>());

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
                CreatedAt = _clock.GetCurrentInstant(),
                UpdatedAt = _clock.GetCurrentInstant()
            }));

        _service = new ConsentService(
            consentRepository,
            _legalDocumentSyncService,
            _notificationInboxService,
            _syncJob,
            _userService,
            serviceProvider,
            _metrics,
            _clock,
            NullLogger<ConsentService>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
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
        var record = await _dbContext.ConsentRecords.FirstAsync();
        record.UserId.Should().Be(userId);
        record.DocumentVersionId.Should().Be(versionId);
        record.IpAddress.Should().Be("192.168.1.1");
        record.UserAgent.Should().Be("TestAgent");
        record.ExplicitConsent.Should().BeTrue();
        record.ConsentedAt.Should().Be(_clock.GetCurrentInstant());
    }

    [HumansFact]
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

    [HumansFact]
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

        var record = await _dbContext.ConsentRecords.FirstAsync();
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

    [HumansFact]
    public async Task SubmitConsentAsync_AfterAdmission_CallsPromoteHookOnceAfterSync()
    {
        var userId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        SeedDocumentVersion(versionId, "Test Doc", new Dictionary<string, string>(StringComparer.Ordinal) { ["es"] = "text" });

        await _service.SubmitConsentAsync(userId, versionId, true, "127.0.0.1", "Agent");

        await _shiftSignupService.Received(1).PromoteWidgetPendingSignupsAfterAdmissionAsync(
            userId, Arg.Any<CancellationToken>());

        // Sequence: Volunteers sync → promote hook → Coordinators sync.
        // Record-only calls inside InOrder; discards keep the analyzers happy.
        Received.InOrder(() =>
        {
            _ = _syncJob.SyncMembershipForUserAsync(userId, SystemTeamType.Volunteers, Arg.Any<CancellationToken>());
            _ = _shiftSignupService.PromoteWidgetPendingSignupsAfterAdmissionAsync(userId, Arg.Any<CancellationToken>());
            _ = _syncJob.SyncMembershipForUserAsync(userId, SystemTeamType.Coordinators, Arg.Any<CancellationToken>());
        });
    }

#pragma warning disable CS0618 // Test stub mirrors the legacy Include(d => d.Team) read path; prod stitches via ITeamService.
    private static ActiveRequiredLegalDocumentSnapshot ToActiveRequiredDocumentSnapshot(LegalDocument document) =>
        new(
            document.Id,
            document.Name,
            document.TeamId,
            document.Team.Name,
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
#pragma warning restore CS0618

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
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        };
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(WrapInUserInfo(stubProfile));

        var result = await _service.SubmitConsentAsync(userId, versionId, true, "127.0.0.1", "Agent");

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("StubProfile");
        (await _dbContext.ConsentRecords.CountAsync()).Should().Be(0);
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
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        };
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(WrapInUserInfo(activeProfile));

        var result = await _service.SubmitConsentAsync(userId, versionId, true, "127.0.0.1", "Agent");

        result.Success.Should().BeTrue();
        (await _dbContext.ConsentRecords.CountAsync()).Should().Be(1);
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
        await _dbContext.SaveChangesAsync();

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
        await _dbContext.SaveChangesAsync();

        var (groups, _) = await _service.GetConsentDashboardAsync(userId);

        groups.Should().HaveCount(1);
        groups[0].Documents.Should().HaveCount(1);
    }

    [HumansFact]
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
        await _dbContext.SaveChangesAsync();

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
        await _dbContext.SaveChangesAsync();

        var (groups, _) = await _service.GetConsentDashboardAsync(userId);

        groups.Should().HaveCount(1);
        groups[0].Documents[0].HasConsented.Should().BeFalse();
    }

    [HumansFact]
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

    [HumansFact]
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
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        };
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(WrapInUserInfo(profile));
        await _dbContext.SaveChangesAsync();

        var detail = await _service.GetConsentReviewDetailAsync(versionId, userId);

        detail.Should().NotBeNull();
        detail!.DocumentName.Should().Be("Test Doc");
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
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        };
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(WrapInUserInfo(profile));
        await _dbContext.SaveChangesAsync();

        var detail = await _service.GetConsentReviewDetailAsync(versionId, userId);

        detail.Should().NotBeNull();
        detail!.HasAlreadyConsented.Should().BeFalse();
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
        detail!.HasAlreadyConsented.Should().BeFalse();
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
        await _dbContext.SaveChangesAsync();

        // Target's chain-follow set includes the source.
        _userService.GetMergedSourceIdsAsync(targetId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlySet<Guid>)new HashSet<Guid> { sourceId });
        // Source tombstone has no further sources.
        _userService.GetMergedSourceIdsAsync(sourceId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlySet<Guid>)new HashSet<Guid>());
        _userService.GetMergedSourceIdsAsync(unrelatedId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlySet<Guid>)new HashSet<Guid>());

        // Input contains both source and target — duplicate-id risk path.
        var result = await _service.GetConsentMapForUsersAsync([sourceId, targetId, unrelatedId]);

        result.Should().ContainKey(targetId);
        result[targetId].Should().Contain(versionId, "target's chain-follow includes the source's explicit consent");
        result.Should().ContainKey(sourceId);
        result.Should().ContainKey(unrelatedId);
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
