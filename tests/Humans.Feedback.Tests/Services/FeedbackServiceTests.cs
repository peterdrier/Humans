using AwesomeAssertions;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Caching;
using Humans.Email.Contracts;
using Humans.Notifications.Contracts;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Application;
using Humans.Domain.Entities;
using Humans.Feedback.Data;
using Humans.Feedback.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;
using FeedbackServiceImpl = Humans.Feedback.Services.FeedbackService;

namespace Humans.Feedback.Tests.Services;

/// <summary>
/// Owns its fixture rather than deriving from <c>Humans.Application.Tests</c>'
/// <c>ServiceTestHarness</c>: that harness is built around an in-memory
/// <c>HumansDbContext</c>, and inheriting it would grant a section test project
/// <c>InternalsVisibleTo</c> on <c>HumansDbContext</c> — the boundary the G5 split exists
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
            Substitute.For<IAuditLogService>(), _navBadge, _cache, Clock,
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

    [HumansFact]
    public async Task UpdateStatusAsync_SetsResolvedFields_WhenTerminal()
    {
        var report = await CreateTestReport();

        await _service.UpdateStatusAsync(report.Id, FeedbackStatus.Resolved, Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        var updated = await FeedbackDb.FeedbackReports.AsNoTracking()
            .FirstAsync(r => r.Id == report.Id, Xunit.TestContext.Current.CancellationToken);
        updated.Status.Should().Be(FeedbackStatus.Resolved);
        updated.ResolvedAt.Should().NotBeNull();
        updated.ResolvedByUserId.Should().NotBeNull();
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
        // BurnerName-is-the-display-name rule: ReporterName must render Profile.BurnerName.
        var userId = Guid.NewGuid();
        SeedUser(userId, "Sparkle", "a@a.com");
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
    }

    [HumansFact]
    public async Task GetActionableCountAsync_CountsOpenWithNoReply_And_AwaitingAdmin()
    {
        var userId = Guid.NewGuid();
        SeedUser(userId, "U", "u@test.com");

        var now = Clock.GetCurrentInstant();

        // Open, no admin message -> actionable
        FeedbackDb.FeedbackReports.Add(new FeedbackReport
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Category = FeedbackCategory.Bug,
            Description = "a",
            PageUrl = "/a",
            Status = FeedbackStatus.Open,
            CreatedAt = now,
            UpdatedAt = now
        });

        // Reporter replied after admin -> actionable
        FeedbackDb.FeedbackReports.Add(new FeedbackReport
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Category = FeedbackCategory.Bug,
            Description = "b",
            PageUrl = "/b",
            Status = FeedbackStatus.Acknowledged,
            CreatedAt = now,
            UpdatedAt = now,
            LastAdminMessageAt = now,
            LastReporterMessageAt = now + Duration.FromMinutes(5)
        });

        // Resolved -> not actionable
        FeedbackDb.FeedbackReports.Add(new FeedbackReport
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Category = FeedbackCategory.Bug,
            Description = "c",
            PageUrl = "/c",
            Status = FeedbackStatus.Resolved,
            CreatedAt = now,
            UpdatedAt = now,
            ResolvedAt = now
        });

        await SaveAllAsync(Xunit.TestContext.Current.CancellationToken);

        var count = await _service.GetActionableCountAsync(Xunit.TestContext.Current.CancellationToken);
        count.Should().Be(2);
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
    // The pre-G5 versions of these wrote User rows into the harness's HumansDbContext and
    // read them back through DB-backed IUserService stubs. A section test project cannot
    // see those tables, so the registry holds the projection the service consumes: UserInfo.

    private UserInfo SeedUser(Guid id, string displayName, string? email = null)
    {
        var user = new User
        {
            Id = id,
            UserName = email ?? $"test-{id}@test.com",
            Email = email,
            DisplayName = displayName,
            PreferredLanguage = "en",
            CreatedAt = Clock.GetCurrentInstant()
        };
        var info = UserInfo.Create(
            user: user,
            userEmails: [],
            eventParticipations: [],
            externalLogins: [],
            profile: null,
            contactFields: [],
            profileLanguages: [],
            volunteerHistory: [],
            communicationPreferences: []);
        _people[id] = info;
        return info;
    }

    private Task SaveAllAsync(CancellationToken ct = default) => FeedbackDb.SaveChangesAsync(ct);
}
