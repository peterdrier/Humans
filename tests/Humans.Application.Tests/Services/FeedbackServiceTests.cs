using AwesomeAssertions;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Repositories.Feedback;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using FeedbackApplicationService = Humans.Application.Services.Feedback.FeedbackService;

namespace Humans.Application.Tests.Services;

public sealed class FeedbackServiceTests : ServiceTestHarness
{
    private readonly IEmailService _emailService;
    private readonly IUserService _userService;
    private readonly IUserEmailService _userEmailService;
    private readonly ITeamService _teamService;
    private readonly INotificationService _notificationService;
    private readonly INavBadgeCacheInvalidator _navBadge;
    private readonly IFeedbackRepository _repository;
    private readonly FeedbackApplicationService _service;

    public FeedbackServiceTests()
        : base(Instant.FromUtc(2026, 3, 18, 12, 0))
    {
        _emailService = Substitute.For<IEmailService>();
        var env = Substitute.For<IHostEnvironment>();
        env.ContentRootPath.Returns(Path.GetTempPath());

        _userService = NewDbBackedUserService();

        _userEmailService = Substitute.For<IUserEmailService>();
        _userEmailService
            .GetNotificationTargetEmailsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var ids = (IReadOnlyCollection<Guid>)call[0]!;
                IReadOnlyDictionary<Guid, string> dict = Db.Users
                    .AsNoTracking()
                    .Where(u => ids.Contains(u.Id) && u.Email != null)
                    .ToDictionary(u => u.Id, u => u.Email!);
                return Task.FromResult(dict);
            });

        _teamService = Substitute.For<ITeamService>();
        _teamService
            .GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var teams = Db.Teams
                    .Include(t => t.Members)
                    .AsNoTracking()
                    .ToList();
                IReadOnlyDictionary<Guid, TeamInfo> dict = teams.ToDictionary(
                    t => t.Id,
                    t => new TeamInfo(
                        t.Id, t.Name, t.Description, t.Slug,
                        t.IsActive, t.IsSystemTeam, t.SystemTeamType, t.RequiresApproval,
                        t.IsPublicPage, t.IsHidden, t.IsPromotedToDirectory,
                        t.CreatedAt,
                        t.Members
                            .Where(m => m.LeftAt is null)
                            .Select(m => new TeamMemberInfo(
                                m.Id, m.UserId, string.Empty, null, null, m.Role, m.JoinedAt))
                            .ToList(),
                        t.ParentTeamId));
                return Task.FromResult(dict);
            });
        _teamService
            .GetTeamAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var id = call.ArgAt<Guid>(0);
                var t = Db.Teams
                    .Include(team => team.Members)
                    .AsNoTracking()
                    .FirstOrDefault(team => team.Id == id);
                if (t is null) return Task.FromResult<TeamInfo?>(null);
                return Task.FromResult<TeamInfo?>(new TeamInfo(
                    t.Id, t.Name, t.Description, t.Slug,
                    t.IsActive, t.IsSystemTeam, t.SystemTeamType, t.RequiresApproval,
                    t.IsPublicPage, t.IsHidden, t.IsPromotedToDirectory,
                    t.CreatedAt,
                    t.Members
                        .Where(m => m.LeftAt is null)
                        .Select(m => new TeamMemberInfo(
                            m.Id, m.UserId, string.Empty, null, null, m.Role, m.JoinedAt))
                        .ToList(),
                    t.ParentTeamId));
            });

        _notificationService = Substitute.For<INotificationService>();
        _navBadge = Substitute.For<INavBadgeCacheInvalidator>();

        _repository = new FeedbackRepository(DbFactory);

        _service = new FeedbackApplicationService(
            _repository, _userService, _userEmailService, _teamService,
            _emailService, _notificationService, AuditLog, _navBadge, Clock, env,
            NullLogger<FeedbackApplicationService>.Instance);
    }

    [HumansFact]
    public async Task SubmitFeedbackAsync_CreatesReport()
    {
        var userId = Guid.NewGuid();
        SeedUser(userId, "Test").Email = "t@t.com";
        await Db.SaveChangesAsync();

        var report = await _service.SubmitFeedbackAsync(
            userId, FeedbackCategory.Bug, "Something broke",
            "/Teams/test", "Mozilla/5.0", null, null);

        report.Id.Should().NotBeEmpty();
        report.Category.Should().Be(FeedbackCategory.Bug);
        report.Status.Should().Be(FeedbackStatus.Open);
        report.Description.Should().Be("Something broke");
        report.PageUrl.Should().Be("/Teams/test");

        _navBadge.Received(1).Invalidate();
    }

    [HumansFact(Timeout = 10000)]
    public async Task SubmitFeedbackAsync_SetsAdditionalContext()
    {
        var userId = Guid.NewGuid();
        SeedUser(userId, "U").Email = "u@test.com";
        await Db.SaveChangesAsync();

        var report = await _service.SubmitFeedbackAsync(
            userId, FeedbackCategory.Bug, "desc", "/page", "UA",
            "Volunteer, Coordinator", null);

        report.AdditionalContext.Should().Be("Volunteer, Coordinator");
    }

    [HumansFact(Timeout = 10000)]
    public async Task SubmitUserFeedbackAsync_BuildsSortedRoleContext()
    {
        var userId = Guid.NewGuid();
        SeedUser(userId, "U").Email = "u@test.com";
        await Db.SaveChangesAsync();

        var report = await _service.SubmitUserFeedbackAsync(
            userId, FeedbackCategory.Bug, "desc", "/page", "UA",
            ["Volunteer", "Admin", "Coordinator"], null);

        report.AdditionalContext.Should().Be("Admin, Coordinator, Volunteer");
    }

    [HumansFact]
    public async Task UpdateStatusAsync_SetsResolvedFields_WhenTerminal()
    {
        var report = await CreateTestReport();

        await _service.UpdateStatusAsync(report.Id, FeedbackStatus.Resolved, Guid.NewGuid());

        var updated = await Db.FeedbackReports.AsNoTracking()
            .FirstAsync(r => r.Id == report.Id);
        updated.Status.Should().Be(FeedbackStatus.Resolved);
        updated.ResolvedAt.Should().NotBeNull();
        updated.ResolvedByUserId.Should().NotBeNull();
    }

    [HumansFact]
    public async Task UpdateStatusAsync_ClearsResolvedFields_WhenReopened()
    {
        var actorId = Guid.NewGuid();
        var report = await CreateTestReport();
        await _service.UpdateStatusAsync(report.Id, FeedbackStatus.Resolved, actorId);
        await _service.UpdateStatusAsync(report.Id, FeedbackStatus.Open, actorId);

        var updated = await Db.FeedbackReports.AsNoTracking()
            .FirstAsync(r => r.Id == report.Id);
        updated.Status.Should().Be(FeedbackStatus.Open);
        updated.ResolvedAt.Should().BeNull();
        updated.ResolvedByUserId.Should().BeNull();
    }

    [HumansFact]
    public async Task GetFeedbackListAsync_FiltersByStatus()
    {
        await CreateTestReport(FeedbackStatus.Open);
        await CreateTestReport(FeedbackStatus.Open);
        await CreateTestReport(FeedbackStatus.Resolved);

        var results = await _service.GetFeedbackListAsync(status: FeedbackStatus.Open);

        results.Should().HaveCount(2);
    }

    [HumansFact]
    public async Task GetFeedbackListAsync_ReturnsReporterInfo()
    {
        var userId = Guid.NewGuid();
        SeedUser(userId, "Alice").Email = "a@a.com";
        await Db.SaveChangesAsync();

        await _service.SubmitFeedbackAsync(
            userId, FeedbackCategory.Bug, "a", "/a", null, null, null);

        var results = await _service.GetFeedbackListAsync();

        results.Should().ContainSingle();
        results[0].ReporterName.Should().Be("Alice");
        results[0].ReporterEmail.Should().Be("a@a.com");
    }

    [HumansFact]
    public async Task GetFeedbackListAsync_ReporterName_PrefersBurnerName()
    {
        // BurnerName-is-the-display-name rule: ReporterName must render Profile.BurnerName.
        var userId = Guid.NewGuid();
        SeedUser(userId, "Sparkle").Email = "a@a.com";
        await Db.SaveChangesAsync();

        await _service.SubmitFeedbackAsync(
            userId, FeedbackCategory.Bug, "a", "/a", null, null, null);

        var results = await _service.GetFeedbackListAsync();

        results.Should().ContainSingle();
        results[0].ReporterName.Should().Be("Sparkle");
    }

    [HumansFact]
    public async Task PostMessageAsync_AdminMessage_SetsLastAdminMessageAt_And_SendsEmail()
    {
        var userId = Guid.NewGuid();
        SeedUser(userId, "Reporter").Email = "reporter@test.com";

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
        Db.FeedbackReports.Add(report);
        await Db.SaveChangesAsync();

        var adminId = Guid.NewGuid();
        var message = await _service.PostMessageAsync(report.Id, adminId, "Looking into it", isAdmin: true);

        message.Content.Should().Be("Looking into it");
        message.SenderUserId.Should().Be(adminId);

        var updated = await Db.FeedbackReports.AsNoTracking()
            .FirstAsync(r => r.Id == report.Id);
        updated.LastAdminMessageAt.Should().NotBeNull();
        updated.LastReporterMessageAt.Should().BeNull();

        await _emailService.Received(1).SendFeedbackResponseAsync(
            "reporter@test.com", "Reporter", "Test", "Looking into it",
            $"/Feedback/{report.Id}", "en", Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetFeedbackByIdForViewerAsync_NonReporter_ReturnsNull()
    {
        var reporterId = Guid.NewGuid();
        SeedUser(reporterId, "Reporter").Email = "reporter@test.com";
        Db.FeedbackReports.Add(new FeedbackReport
        {
            Id = Guid.NewGuid(),
            UserId = reporterId,
            Category = FeedbackCategory.Bug,
            Description = "Test",
            PageUrl = "/test",
            Status = FeedbackStatus.Open,
            CreatedAt = Clock.GetCurrentInstant(),
            UpdatedAt = Clock.GetCurrentInstant()
        });
        await Db.SaveChangesAsync();

        var result = await _service.GetFeedbackByIdForViewerAsync(
            Db.FeedbackReports.Single().Id,
            Guid.NewGuid(),
            isAdmin: false);

        result.Should().BeNull();
    }

    [HumansFact]
    public async Task PostMessageAsync_ReporterMessage_SetsLastReporterMessageAt_NoEmail()
    {
        var userId = Guid.NewGuid();
        SeedUser(userId, "Reporter").Email = "reporter@test.com";

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
        Db.FeedbackReports.Add(report);
        await Db.SaveChangesAsync();

        var message = await _service.PostMessageAsync(report.Id, userId, "More details", isAdmin: false);

        message.Content.Should().Be("More details");
        var updated = await Db.FeedbackReports.AsNoTracking()
            .FirstAsync(r => r.Id == report.Id);
        updated.LastReporterMessageAt.Should().NotBeNull();
        updated.LastAdminMessageAt.Should().BeNull();

        await _emailService.DidNotReceive().SendFeedbackResponseAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetActionableCountAsync_CountsOpenWithNoReply_And_AwaitingAdmin()
    {
        var userId = Guid.NewGuid();
        SeedUser(userId, "U").Email = "u@test.com";

        var now = Clock.GetCurrentInstant();

        // Open, no admin message -> actionable
        Db.FeedbackReports.Add(new FeedbackReport
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
        Db.FeedbackReports.Add(new FeedbackReport
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
        Db.FeedbackReports.Add(new FeedbackReport
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

        await Db.SaveChangesAsync();

        var count = await _service.GetActionableCountAsync();
        count.Should().Be(2);
    }

    [HumansFact]
    public async Task GetDistinctReportersAsync_ResolvesNamesFromUserService_AndOrdersAlphabetically()
    {
        var bobId = Guid.NewGuid();
        var aliceId = Guid.NewGuid();
        SeedUser(bobId, "Bob").Email = "b@b.com";
        SeedUser(aliceId, "Alice").Email = "a@a.com";
        var now = Clock.GetCurrentInstant();
        Db.FeedbackReports.Add(new FeedbackReport
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
        Db.FeedbackReports.Add(new FeedbackReport
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
        Db.FeedbackReports.Add(new FeedbackReport
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
        await Db.SaveChangesAsync();

        var reporters = await _service.GetDistinctReportersAsync();

        reporters.Should().HaveCount(2);
        reporters[0].DisplayName.Should().Be("Alice");
        reporters[0].Count.Should().Be(1);
        reporters[1].DisplayName.Should().Be("Bob");
        reporters[1].Count.Should().Be(2);
    }

    private async Task<FeedbackReport> CreateTestReport(FeedbackStatus status = FeedbackStatus.Open)
    {
        var userId = Guid.NewGuid();
        SeedUser(userId, "Test").Email = $"{userId}@test.com";
        await Db.SaveChangesAsync();

        var report = await _service.SubmitFeedbackAsync(
            userId, FeedbackCategory.Bug, "Test bug", "/test", null, null, null);

        if (status != FeedbackStatus.Open)
        {
            await _service.UpdateStatusAsync(report.Id, status, Guid.NewGuid());
        }

        return report;
    }
}
