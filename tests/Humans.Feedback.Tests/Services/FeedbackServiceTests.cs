using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Base.Interfaces;
using Humans.Base.Interfaces.Caching;
using Humans.Email.Contracts;
using Humans.Notifications.Contracts;
using Humans.Users.Contracts;
using Humans.Teams.Contracts;
using Humans.Feedback.Data;
using Humans.Feedback.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;
using Humans.Base.Caching;
using FeedbackServiceImpl = Humans.Feedback.Services.FeedbackService;
using Humans.Feedback.Contracts;

namespace Humans.Feedback.Tests.Services;

/// <summary>
/// Owns its fixture rather than deriving from <c>Humans.Application.Tests</c>'
/// <c>ServiceTestHarness</c>: that harness is built around an in-memory
/// <c>UsersDbContext</c>, and inheriting it would grant a section test project
/// <c>InternalsVisibleTo</c> on <c>UsersDbContext</c> — the boundary the G5 split exists
/// to draw (nobodies-collective/Humans#866). The users and teams the service reads back
/// live in an in-memory registry the <c>Seed*</c> helpers write to, so the test bodies
/// below are unchanged from their pre-move versions.
/// </summary>
public sealed class FeedbackServiceTests
{
    private readonly FakeClock Clock = new(Instant.FromUtc(2026, 3, 18, 12, 0));

    private readonly TestDbContextFactory<FeedbackDbContext> FeedbackDbFactory =
        new(new DbContextOptionsBuilder<FeedbackDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private readonly FeedbackDbContext FeedbackDb;

    private readonly Dictionary<Guid, UserInfo> _people = [];
    private readonly Dictionary<Guid, TeamInfo> _teams = [];

    private readonly IEmailService _emailService = Substitute.For<IEmailService>();
    private readonly IEmailMessageFactory _emailMessages = Substitute.For<IEmailMessageFactory>();
    private readonly INotificationEmitter _notificationService = Substitute.For<INotificationEmitter>();
    private readonly IAuditLogService _auditLog = Substitute.For<IAuditLogService>();
    private readonly IFileStorage _fileStorage = Substitute.For<IFileStorage>();
    private readonly INavBadgeCacheInvalidator _navBadge = Substitute.For<INavBadgeCacheInvalidator>();
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly IFeedbackRepository _repository;
    private readonly FeedbackServiceImpl _service;

    public FeedbackServiceTests()
    {
        FeedbackDb = FeedbackDbFactory.CreateDbContext();

        var userService = Substitute.For<IUserServiceRead>();
        var userEmailService = Substitute.For<IUserEmailService>();
        var teamService = Substitute.For<ITeamServiceRead>();

        userService
            .GetUserInfoAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => new ValueTask<UserInfo?>(_people.GetValueOrDefault(call.ArgAt<Guid>(0))));

        userService
            .GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyDictionary<Guid, UserInfo>)call
                .ArgAt<IReadOnlyCollection<Guid>>(0)
                .Where(_people.ContainsKey)
                .ToDictionary(id => id, id => _people[id]));

        userEmailService
            .GetNotificationTargetEmailsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyDictionary<Guid, string>)call
                .ArgAt<IReadOnlyCollection<Guid>>(0)
                .Where(id => _people.TryGetValue(id, out var p) && !string.IsNullOrEmpty(p.Email))
                .ToDictionary(id => id, id => _people[id].Email!));

        teamService
            .GetTeamAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => _teams.GetValueOrDefault(call.ArgAt<Guid>(0)));

        teamService
            .GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(_ => (IReadOnlyDictionary<Guid, TeamInfo>)new Dictionary<Guid, TeamInfo>(_teams));

        _repository = new FeedbackRepository(FeedbackDbFactory);

        _service = new FeedbackServiceImpl(
            _repository, userService, userEmailService, teamService,
            _emailService, _emailMessages, _notificationService,
            _auditLog, _navBadge,
            _fileStorage, _cache, Clock,
            NullLogger<FeedbackServiceImpl>.Instance);
    }

    // Feedback stopped accepting new reports in nobodies-collective/Humans#977 —
    // there is no service- or repository-level create any more, so every fixture
    // below seeds historical rows straight into the DB.
    [HumansFact]
    public void FeedbackSurface_ExposesNoReportCreationMethod()
    {
        Type[] surfaces = [typeof(FeedbackServiceImpl), typeof(IFeedbackRepository)];

        var creators = surfaces
            .SelectMany(s => s.GetMethods().Select(m => $"{s.Name}.{m.Name}"))
            .Where(n => n.Contains(".Submit", StringComparison.Ordinal)
                     || n.Contains(".Create", StringComparison.Ordinal)
                     || n.Contains(".AddReport", StringComparison.Ordinal))
            .ToList();

        creators.Should().BeEmpty("no Feedback surface may expose a way to create a FeedbackReport");
    }

    [HumansTheory]
    [InlineData(FeedbackStatus.Resolved)]
    [InlineData(FeedbackStatus.WontFix)]
    public async Task UpdateStatusAsync_SetsResolvedFields_WhenTerminal(FeedbackStatus terminal)
    {
        var report = await CreateTestReport();

        await _service.UpdateStatusAsync(report.Id, terminal, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        var updated = await FeedbackDb.FeedbackReports.AsNoTracking()
            .FirstAsync(r => r.Id == report.Id, Xunit.TestContext.Current.CancellationToken);
        updated.Status.Should().Be(terminal);
        updated.ResolvedAt.Should().NotBeNull();
        updated.ResolvedByUserId.Should().NotBeNull();
    }

    [HumansFact]
    public async Task UpdateStatusAsync_Audits_WithActor_OrApiFallback()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var actorId = Guid.NewGuid();
        var report = await CreateTestReport();

        await _service.UpdateStatusAsync(report.Id, FeedbackStatus.Acknowledged, actorId, ct);
        await _auditLog.Received(1).LogAsync(
            AuditAction.FeedbackStatusChanged, "FeedbackReport", report.Id,
            Arg.Any<string>(), actorId);

        await _service.UpdateStatusAsync(report.Id, FeedbackStatus.Open, null, ct);
        await _auditLog.Received(1).LogAsync(
            AuditAction.FeedbackStatusChanged, "FeedbackReport", report.Id,
            Arg.Any<string>(), "API");
    }

    [HumansFact]
    public async Task UpdateStatusAsync_ClearsResolvedFields_WhenReopened()
    {
        var actorId = Guid.NewGuid();
        var report = await CreateTestReport();
        await _service.UpdateStatusAsync(report.Id, FeedbackStatus.Resolved, actorId, Xunit.TestContext.Current.CancellationToken);
        await _service.UpdateStatusAsync(report.Id, FeedbackStatus.Open, actorId, Xunit.TestContext.Current.CancellationToken);

        var updated = await FeedbackDb.FeedbackReports.AsNoTracking()
            .FirstAsync(r => r.Id == report.Id, Xunit.TestContext.Current.CancellationToken);
        updated.Status.Should().Be(FeedbackStatus.Open);
        updated.ResolvedAt.Should().BeNull();
        updated.ResolvedByUserId.Should().BeNull();
    }

    [HumansFact]
    public async Task GetFeedbackListAsync_FiltersByStatus()
    {
        await CreateTestReport();
        await CreateTestReport();
        await CreateTestReport(FeedbackStatus.Resolved);

        var results = await _service.GetFeedbackListAsync(status: FeedbackStatus.Open, cancellationToken: Xunit.TestContext.Current.CancellationToken);

        results.Should().HaveCount(2);
    }

    [HumansFact]
    public async Task GetFeedbackListAsync_ReturnsReporterInfo()
    {
        var userId = Guid.NewGuid();
        SeedUser(userId, "Alice", "a@a.com");
        await SeedReportAsync(userId, "a", "/a");

        var results = await _service.GetFeedbackListAsync(cancellationToken: Xunit.TestContext.Current.CancellationToken);

        results.Should().ContainSingle();
        results[0].ReporterName.Should().Be("Alice");
        results[0].ReporterEmail.Should().Be("a@a.com");
    }

    [HumansFact]
    public async Task GetFeedbackListAsync_ReporterName_PrefersBurnerName()
    {
        // BurnerName-is-the-display-name rule: ReporterName must render UserInfo.BurnerName,
        // not the legacy DisplayName — so the two must differ for this test to be able to fail.
        var userId = Guid.NewGuid();
        SeedUser(userId, "Legal Name", "a@a.com", burnerName: "Sparkle");
        await SeedReportAsync(userId, "a", "/a");

        var results = await _service.GetFeedbackListAsync(cancellationToken: Xunit.TestContext.Current.CancellationToken);

        results.Should().ContainSingle();
        results[0].ReporterName.Should().Be("Sparkle");
    }

    [HumansFact]
    public async Task PostMessageAsync_AdminMessage_SetsLastAdminMessageAt_And_SendsEmail()
    {
        var userId = Guid.NewGuid();
        SeedUser(userId, "Reporter", "reporter@test.com");

        var report = new FeedbackReport
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Category = FeedbackCategory.Bug,
            Description = "Test",
            PageUrl = "/test",
            Status = FeedbackStatus.Open,
            CreatedAt = Clock.GetCurrentInstant(),
            UpdatedAt = Clock.GetCurrentInstant()
        };
        FeedbackDb.FeedbackReports.Add(report);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var adminId = Guid.NewGuid();
        var message = await _service.PostMessageAsync(report.Id, adminId, "Looking into it", Xunit.TestContext.Current.CancellationToken);

        message.Content.Should().Be("Looking into it");
        message.SenderUserId.Should().Be(adminId);

        var updated = await FeedbackDb.FeedbackReports.AsNoTracking()
            .FirstAsync(r => r.Id == report.Id, Xunit.TestContext.Current.CancellationToken);
        updated.LastAdminMessageAt.Should().NotBeNull();
        updated.LastReporterMessageAt.Should().BeNull();

        _emailMessages.Received(1).FeedbackResponse(
            "reporter@test.com", "Reporter", "Test", "Looking into it",
            $"/Feedback/{report.Id}", "en");
        await _emailService.Received(1).SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
        await _notificationService.Received(1).SendAsync(
            NotificationSource.FeedbackResponse,
            NotificationClass.Informational,
            NotificationPriority.Normal,
            Arg.Any<string>(),
            Arg.Is<IReadOnlyList<Guid>>(r => r.Count == 1 && r[0] == userId),
            body: Arg.Any<string?>(),
            actionUrl: Arg.Any<string?>(),
            actionLabel: Arg.Any<string?>(),
            targetGroupName: Arg.Any<string?>(),
            sourceKey: Arg.Any<string?>(),
            cancellationToken: Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task PostMessageAsync_EmailThrow_PersistsNothing()
    {
        // The email is sent before the persist on purpose: an SMTP throw must
        // leave no committed message row and no LastAdminMessageAt, so the admin
        // can simply retry.
        var ct = Xunit.TestContext.Current.CancellationToken;
        var userId = Guid.NewGuid();
        SeedUser(userId, "Reporter", "reporter@test.com");
        var report = await SeedReportAsync(userId, "Test", "/test");

        _emailService.SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("SMTP down"));

        var act = () => _service.PostMessageAsync(report.Id, Guid.NewGuid(), "reply", ct);
        await act.Should().ThrowAsync<InvalidOperationException>();

        (await FeedbackDb.FeedbackMessages.AsNoTracking().CountAsync(ct)).Should().Be(0);
        var unchanged = await FeedbackDb.FeedbackReports.AsNoTracking()
            .FirstAsync(r => r.Id == report.Id, ct);
        unchanged.LastAdminMessageAt.Should().BeNull();
    }

    [HumansFact]
    public async Task PostMessageAsync_NotifierThrow_IsBestEffort()
    {
        // The in-app notification is best-effort: a notifier failure must not
        // undo or fail a reply whose email already went out.
        var ct = Xunit.TestContext.Current.CancellationToken;
        var userId = Guid.NewGuid();
        SeedUser(userId, "Reporter", "reporter@test.com");
        var report = await SeedReportAsync(userId, "Test", "/test");

        _notificationService.SendAsync(
                Arg.Any<NotificationSource>(), Arg.Any<NotificationClass>(),
                Arg.Any<NotificationPriority>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<Guid>>(),
                body: Arg.Any<string?>(), actionUrl: Arg.Any<string?>(),
                actionLabel: Arg.Any<string?>(), targetGroupName: Arg.Any<string?>(),
                sourceKey: Arg.Any<string?>(), cancellationToken: Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("notifier down"));

        var message = await _service.PostMessageAsync(report.Id, Guid.NewGuid(), "reply", ct);

        message.Content.Should().Be("reply");
        (await FeedbackDb.FeedbackMessages.AsNoTracking().CountAsync(ct)).Should().Be(1);
    }

    [HumansFact]
    public async Task UpdateAssignmentAsync_PersistsBothColumns_AndAudits()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var actorId = Guid.NewGuid();
        var assigneeId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        SeedUser(assigneeId, "Assignee");
        var report = await CreateTestReport();

        await _service.UpdateAssignmentAsync(report.Id, assigneeId, teamId, actorId, ct);

        var updated = await FeedbackDb.FeedbackReports.AsNoTracking()
            .FirstAsync(r => r.Id == report.Id, ct);
        updated.AssignedToUserId.Should().Be(assigneeId);
        updated.AssignedToTeamId.Should().Be(teamId);
        await _auditLog.Received(1).LogAsync(
            AuditAction.FeedbackAssignmentChanged, "FeedbackReport", report.Id,
            Arg.Is<string>(s => s.Contains("Assignee") && s.Contains("Team")), actorId);
    }

    [HumansFact]
    public async Task UpdateAssignmentAsync_NoChange_WritesNoAudit()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var report = await CreateTestReport();

        await _service.UpdateAssignmentAsync(report.Id, null, null, Guid.NewGuid(), ct);

        await _auditLog.DidNotReceiveWithAnyArgs().LogAsync(
            default, default!, default, default!, default(Guid));
        await _auditLog.DidNotReceiveWithAnyArgs().LogAsync(
            default, default!, default, default!, default(string)!);
    }

    [HumansFact]
    public async Task SetGitHubIssueNumberAsync_SetsAndClears_AndAudits()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var actorId = Guid.NewGuid();
        var report = await CreateTestReport();

        await _service.SetGitHubIssueNumberAsync(report.Id, 123, actorId, ct);
        (await FeedbackDb.FeedbackReports.AsNoTracking().FirstAsync(r => r.Id == report.Id, ct))
            .GitHubIssueNumber.Should().Be(123);
        await _auditLog.Received(1).LogAsync(
            AuditAction.FeedbackGitHubLinked, "FeedbackReport", report.Id,
            Arg.Is<string>(s => s.Contains("123")), actorId);

        // The API path (no actor) clears the link and audits as "API".
        await _service.SetGitHubIssueNumberAsync(report.Id, null, null, ct);
        (await FeedbackDb.FeedbackReports.AsNoTracking().FirstAsync(r => r.Id == report.Id, ct))
            .GitHubIssueNumber.Should().BeNull();
        await _auditLog.Received(1).LogAsync(
            AuditAction.FeedbackGitHubLinked, "FeedbackReport", report.Id,
            Arg.Is<string>(s => s.Contains("cleared")), "API");
    }

    [HumansFact]
    public async Task EraseForUserAsync_DeletesOwnRows_DetachesForeignFootprint()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var erasedId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var now = Clock.GetCurrentInstant();

        // Own report, with a screenshot blob to clean up.
        FeedbackDb.FeedbackReports.Add(new FeedbackReport
        {
            Id = Guid.NewGuid(),
            UserId = erasedId,
            Category = FeedbackCategory.Bug,
            Description = "mine",
            PageUrl = "/mine",
            ScreenshotStoragePath = "uploads/feedback/x/shot.png",
            Status = FeedbackStatus.Open,
            CreatedAt = now,
            UpdatedAt = now
        });

        // Someone else's report where the erased user left a reply and holds triage links.
        var foreignReport = new FeedbackReport
        {
            Id = Guid.NewGuid(),
            UserId = otherId,
            Category = FeedbackCategory.Bug,
            Description = "theirs",
            PageUrl = "/theirs",
            Status = FeedbackStatus.Resolved,
            ResolvedByUserId = erasedId,
            AssignedToUserId = erasedId,
            CreatedAt = now,
            UpdatedAt = now
        };
        FeedbackDb.FeedbackReports.Add(foreignReport);
        var foreignReply = new FeedbackMessage
        {
            Id = Guid.NewGuid(),
            FeedbackReportId = foreignReport.Id,
            SenderUserId = erasedId,
            Content = "their thread keeps this",
            CreatedAt = now
        };
        FeedbackDb.FeedbackMessages.Add(foreignReply);
        await SaveAllAsync(ct);

        await _service.EraseForUserAsync(erasedId, ct);

        // Own report hard-deleted; its screenshot handed to IFileStorage.
        (await FeedbackDb.FeedbackReports.AsNoTracking().AnyAsync(r => r.UserId == erasedId, ct))
            .Should().BeFalse();
        await _fileStorage.Received(1).DeleteAsync("uploads/feedback/x/shot.png", Arg.Any<CancellationToken>());

        // The other human's thread survives with the erased user detached everywhere.
        var survivor = await FeedbackDb.FeedbackReports.AsNoTracking()
            .FirstAsync(r => r.Id == foreignReport.Id, ct);
        survivor.ResolvedByUserId.Should().BeNull();
        survivor.AssignedToUserId.Should().BeNull();
        var reply = await FeedbackDb.FeedbackMessages.AsNoTracking()
            .FirstAsync(m => m.Id == foreignReply.Id, ct);
        reply.Content.Should().Be("their thread keeps this");
        reply.SenderUserId.Should().BeNull();
    }

    [HumansFact]
    public async Task ReassignAsync_RepointsReportsAndMessages_ToTargetUser()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var sourceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var report = await SeedReportAsync(sourceId, "source's report", "/s");
        var otherReport = await SeedReportAsync(otherId, "other's report", "/o");
        FeedbackDb.FeedbackMessages.Add(new FeedbackMessage
        {
            Id = Guid.NewGuid(),
            FeedbackReportId = otherReport.Id,
            SenderUserId = sourceId,
            Content = "reply by source",
            CreatedAt = Clock.GetCurrentInstant()
        });
        await SaveAllAsync(ct);
        var mergeStamp = Clock.GetCurrentInstant() + Duration.FromHours(1);

        await _service.ReassignAsync(sourceId, targetId, Guid.NewGuid(), mergeStamp, ct);

        var movedReport = await FeedbackDb.FeedbackReports.AsNoTracking()
            .FirstAsync(r => r.Id == report.Id, ct);
        movedReport.UserId.Should().Be(targetId);
        movedReport.UpdatedAt.Should().Be(mergeStamp);
        var movedMessage = await FeedbackDb.FeedbackMessages.AsNoTracking()
            .FirstAsync(m => m.FeedbackReportId == otherReport.Id, ct);
        movedMessage.SenderUserId.Should().Be(targetId);
        (await FeedbackDb.FeedbackReports.AsNoTracking().AnyAsync(r => r.UserId == sourceId, ct))
            .Should().BeFalse();
    }

    [HumansFact]
    public async Task GetActionableCountAsync_ServesFromCache_UntilBadgeKeyEvicted()
    {
        // What counts as actionable is the repository's rule and is pinned in
        // FeedbackRepositoryTests — the service's own contribution is the 2-min
        // FeedbackBadgeCount cache in front of it, so that is what this pins.
        var ct = Xunit.TestContext.Current.CancellationToken;
        var userId = Guid.NewGuid();
        SeedUser(userId, "U", "u@test.com");
        await SeedReportAsync(userId, "a", "/a");

        (await _service.GetActionableCountAsync(ct)).Should().Be(1);

        await SeedReportAsync(userId, "b", "/b");
        (await _service.GetActionableCountAsync(ct)).Should().Be(1, "the count is cached");

        // What INavBadgeCacheInvalidator does to this cache.
        _cache.Remove(CacheKeys.FeedbackBadgeCount);
        (await _service.GetActionableCountAsync(ct)).Should().Be(2, "eviction forces a recount");
    }

    [HumansFact]
    public async Task GetDistinctReportersAsync_ResolvesNamesFromUserService_AndOrdersAlphabetically()
    {
        var bobId = Guid.NewGuid();
        var aliceId = Guid.NewGuid();
        SeedUser(bobId, "Bob", "b@b.com");
        SeedUser(aliceId, "Alice", "a@a.com");
        var now = Clock.GetCurrentInstant();
        FeedbackDb.FeedbackReports.Add(new FeedbackReport
        {
            Id = Guid.NewGuid(),
            UserId = bobId,
            Category = FeedbackCategory.Bug,
            Description = "b",
            PageUrl = "/b",
            Status = FeedbackStatus.Open,
            CreatedAt = now,
            UpdatedAt = now
        });
        FeedbackDb.FeedbackReports.Add(new FeedbackReport
        {
            Id = Guid.NewGuid(),
            UserId = bobId,
            Category = FeedbackCategory.Bug,
            Description = "b2",
            PageUrl = "/b2",
            Status = FeedbackStatus.Open,
            CreatedAt = now,
            UpdatedAt = now
        });
        FeedbackDb.FeedbackReports.Add(new FeedbackReport
        {
            Id = Guid.NewGuid(),
            UserId = aliceId,
            Category = FeedbackCategory.Bug,
            Description = "a",
            PageUrl = "/a",
            Status = FeedbackStatus.Open,
            CreatedAt = now,
            UpdatedAt = now
        });
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var reporters = await _service.GetDistinctReportersAsync(Xunit.TestContext.Current.CancellationToken);

        reporters.Should().HaveCount(2);
        reporters[0].DisplayName.Should().Be("Alice");
        reporters[0].Count.Should().Be(1);
        reporters[1].DisplayName.Should().Be("Bob");
        reporters[1].Count.Should().Be(2);
    }

    private async Task<FeedbackReport> CreateTestReport(FeedbackStatus status = FeedbackStatus.Open)
    {
        var userId = Guid.NewGuid();
        SeedUser(userId, "Test", $"{userId}@test.com");
        var report = await SeedReportAsync(userId, "Test bug", "/test");

        if (status != FeedbackStatus.Open)
        {
            await _service.UpdateStatusAsync(report.Id, status, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);
        }

        return report;
    }

    /// <summary>
    /// Seeds a historical report straight into the DB. Feedback has no creation
    /// path any more (nobodies-collective/Humans#977), so tests must not go
    /// through the service to get one.
    /// </summary>
    private async Task<FeedbackReport> SeedReportAsync(
        Guid userId, string description, string pageUrl, FeedbackStatus status = FeedbackStatus.Open)
    {
        var now = Clock.GetCurrentInstant();
        var report = new FeedbackReport
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Category = FeedbackCategory.Bug,
            Description = description,
            PageUrl = pageUrl,
            Status = status,
            CreatedAt = now,
            UpdatedAt = now
        };
        FeedbackDb.FeedbackReports.Add(report);
        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);
        return report;
    }

    // ----- People and teams -----------------------------------------------------
    // The pre-G5 versions of these wrote User rows into the harness's UsersDbContext and
    // read them back through DB-backed IUserService stubs. A section test project cannot
    // see those tables, so the registry holds the projection the service consumes: UserInfo.

    private UserInfo SeedUser(Guid id, string displayName, string? email = null, string? burnerName = null)
    {
        var user = new User
        {
            Id = id,
            UserName = email ?? $"test-{id}@test.com",
            Email = email,
            DisplayName = displayName,
            BurnerName = burnerName,
            PreferredLanguage = "en",
            CreatedAt = Clock.GetCurrentInstant()
        };
        var info = UserInfo.Create(
            user: user,
            userEmails: [],
            eventParticipations: [],
            externalLogins: [],
            profile: null,
            communicationPreferences: []);
        _people[id] = info;
        return info;
    }

    private Task SaveAllAsync(CancellationToken ct = default) => FeedbackDb.SaveChangesAsync(ct);
}
