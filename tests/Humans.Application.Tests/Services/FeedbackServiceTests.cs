using AwesomeAssertions;
using Humans.Application.Interfaces.AuditLog;
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
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories.Feedback;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;
using FeedbackApplicationService = Humans.Application.Services.Feedback.FeedbackService;

namespace Humans.Application.Tests.Services;

public class FeedbackServiceTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly IEmailService _emailService;
    private readonly IAuditLogService _auditLog;
    private readonly IUserService _userService;
    private readonly IUserEmailService _userEmailService;
    private readonly ITeamService _teamService;
    private readonly INotificationService _notificationService;
    private readonly INavBadgeCacheInvalidator _navBadge;
    private readonly IFeedbackRepository _repository;
    private readonly FeedbackApplicationService _service;

    public FeedbackServiceTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new HumansDbContext(options);
        _clock = new FakeClock(Instant.FromUtc(2026, 3, 18, 12, 0));
        _emailService = Substitute.For<IEmailService>();
        _auditLog = Substitute.For<IAuditLogService>();
        var env = Substitute.For<IHostEnvironment>();
        env.ContentRootPath.Returns(Path.GetTempPath());

        _userService = Substitute.For<IUserService>();
        _userService
            .GetByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var ids = (IReadOnlyCollection<Guid>)call[0]!;
                var dict = _dbContext.Users
                    .AsNoTracking()
                    .Where(u => ids.Contains(u.Id))
                    .ToDictionary(u => u.Id);
                return Task.FromResult<IReadOnlyDictionary<Guid, User>>(dict);
            });
        _userService
            .GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var id = (Guid)call[0]!;
                return Task.FromResult(_dbContext.Users.AsNoTracking().FirstOrDefault(u => u.Id == id));
            });

        _userEmailService = Substitute.For<IUserEmailService>();
        _userEmailService
            .GetNotificationTargetEmailsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var ids = (IReadOnlyCollection<Guid>)call[0]!;
                IReadOnlyDictionary<Guid, string> dict = _dbContext.Users
                    .AsNoTracking()
                    .Where(u => ids.Contains(u.Id) && u.Email != null)
                    .ToDictionary(u => u.Id, u => u.Email!);
                return Task.FromResult(dict);
            });

        _teamService = Substitute.For<ITeamService>();
        _teamService
            .GetTeamNamesByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var ids = (IReadOnlyCollection<Guid>)call[0]!;
                IReadOnlyDictionary<Guid, string> dict = _dbContext.Teams
                    .AsNoTracking()
                    .Where(t => ids.Contains(t.Id))
                    .ToDictionary(t => t.Id, t => t.Name);
                return Task.FromResult(dict);
            });

        _notificationService = Substitute.For<INotificationService>();
        _navBadge = Substitute.For<INavBadgeCacheInvalidator>();

        _repository = new FeedbackRepository(new TestDbContextFactory(options));

        _service = new FeedbackApplicationService(
            _repository, _userService, _userEmailService, _teamService,
            _emailService, _notificationService, _auditLog, _navBadge, _clock, env,
            NullLogger<FeedbackApplicationService>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [HumansFact]
    public async Task SubmitFeedbackAsync_CreatesReport()
    {
        var userId = Guid.NewGuid();
        _dbContext.Users.Add(new User { Id = userId, DisplayName = "Test", Email = "t@t.com" });
        await _dbContext.SaveChangesAsync();

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
        _dbContext.Users.Add(new User { Id = userId, Email = "u@test.com", DisplayName = "U" });
        await _dbContext.SaveChangesAsync();

        var report = await _service.SubmitFeedbackAsync(
            userId, FeedbackCategory.Bug, "desc", "/page", "UA",
            "Volunteer, Coordinator", null);

        report.AdditionalContext.Should().Be("Volunteer, Coordinator");
    }

    [HumansFact]
    public async Task UpdateStatusAsync_SetsResolvedFields_WhenTerminal()
    {
        var report = await CreateTestReport();

        await _service.UpdateStatusAsync(report.Id, FeedbackStatus.Resolved, Guid.NewGuid());

        var updated = await _dbContext.FeedbackReports.AsNoTracking()
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

        var updated = await _dbContext.FeedbackReports.AsNoTracking()
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
    public async Task GetFeedbackListAsync_StitchesReporterNav()
    {
        var userId = Guid.NewGuid();
        _dbContext.Users.Add(new User { Id = userId, DisplayName = "Alice", Email = "a@a.com" });
        await _dbContext.SaveChangesAsync();

        await _service.SubmitFeedbackAsync(
            userId, FeedbackCategory.Bug, "a", "/a", null, null, null);

        var results = await _service.GetFeedbackListAsync();

        results.Should().ContainSingle();
#pragma warning disable CS0618
        results[0].User.Should().NotBeNull();
        results[0].User.DisplayName.Should().Be("Alice");
#pragma warning restore CS0618
    }

    [HumansFact]
    public async Task PostMessageAsync_AdminMessage_SetsLastAdminMessageAt_And_SendsEmail()
    {
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            Email = "reporter@test.com",
            DisplayName = "Reporter",
            PreferredLanguage = "en"
        };
        _dbContext.Users.Add(user);

        var report = new FeedbackReport
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Category = FeedbackCategory.Bug,
            Description = "Test",
            PageUrl = "/test",
            Status = FeedbackStatus.Open,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        };
        _dbContext.FeedbackReports.Add(report);
        await _dbContext.SaveChangesAsync();

        var adminId = Guid.NewGuid();
        var message = await _service.PostMessageAsync(report.Id, adminId, "Looking into it", isAdmin: true);

        message.Content.Should().Be("Looking into it");
        message.SenderUserId.Should().Be(adminId);

        var updated = await _dbContext.FeedbackReports.AsNoTracking()
            .FirstAsync(r => r.Id == report.Id);
        updated.LastAdminMessageAt.Should().NotBeNull();
        updated.LastReporterMessageAt.Should().BeNull();

        await _emailService.Received(1).SendFeedbackResponseAsync(
            "reporter@test.com", "Reporter", "Test", "Looking into it",
            $"/Feedback/{report.Id}", "en", Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task PostMessageAsync_ReporterMessage_SetsLastReporterMessageAt_NoEmail()
    {
        var userId = Guid.NewGuid();
        var user = new User { Id = userId, Email = "reporter@test.com", DisplayName = "Reporter" };
        _dbContext.Users.Add(user);

        var report = new FeedbackReport
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Category = FeedbackCategory.Bug,
            Description = "Test",
            PageUrl = "/test",
            Status = FeedbackStatus.Open,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        };
        _dbContext.FeedbackReports.Add(report);
        await _dbContext.SaveChangesAsync();

        var message = await _service.PostMessageAsync(report.Id, userId, "More details", isAdmin: false);

        message.Content.Should().Be("More details");
        var updated = await _dbContext.FeedbackReports.AsNoTracking()
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
        var user = new User { Id = userId, Email = "u@test.com", DisplayName = "U" };
        _dbContext.Users.Add(user);

        var now = _clock.GetCurrentInstant();

        // Open, no admin message -> actionable
        _dbContext.FeedbackReports.Add(new FeedbackReport
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
        _dbContext.FeedbackReports.Add(new FeedbackReport
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
        _dbContext.FeedbackReports.Add(new FeedbackReport
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

        await _dbContext.SaveChangesAsync();

        var count = await _service.GetActionableCountAsync();
        count.Should().Be(2);
    }

    [HumansFact]
    public async Task GetDistinctReportersAsync_ResolvesNamesFromUserService_AndOrdersAlphabetically()
    {
        var bobId = Guid.NewGuid();
        var aliceId = Guid.NewGuid();
        _dbContext.Users.Add(new User { Id = bobId, DisplayName = "Bob", Email = "b@b.com" });
        _dbContext.Users.Add(new User { Id = aliceId, DisplayName = "Alice", Email = "a@a.com" });
        var now = _clock.GetCurrentInstant();
        _dbContext.FeedbackReports.Add(new FeedbackReport
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
        _dbContext.FeedbackReports.Add(new FeedbackReport
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
        _dbContext.FeedbackReports.Add(new FeedbackReport
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
        await _dbContext.SaveChangesAsync();

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
        _dbContext.Users.Add(new User { Id = userId, DisplayName = "Test", Email = $"{userId}@test.com" });
        await _dbContext.SaveChangesAsync();

        var report = await _service.SubmitFeedbackAsync(
            userId, FeedbackCategory.Bug, "Test bug", "/test", null, null, null);

        if (status != FeedbackStatus.Open)
        {
            await _service.UpdateStatusAsync(report.Id, status, Guid.NewGuid());
        }

        return report;
    }
}
