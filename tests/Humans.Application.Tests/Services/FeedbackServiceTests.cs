using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Services;

public class FeedbackServiceTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly IEmailService _emailService;
    private readonly IAuditLogService _auditLog;
    private readonly FeedbackService _service;

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

        var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());

        var notificationService = Substitute.For<INotificationService>();

        _service = new FeedbackService(
            _dbContext, _emailService, notificationService, _auditLog, _clock, cache, env,
            NullLogger<FeedbackService>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
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
    }

    [Fact]
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

    [Fact]
    public async Task UpdateStatusAsync_SetsResolvedFields_WhenTerminal()
    {
        var report = await CreateTestReport();

        await _service.UpdateStatusAsync(report.Id, FeedbackStatus.Resolved, Guid.NewGuid());

        var updated = await _dbContext.FeedbackReports.FindAsync(report.Id);
        updated!.Status.Should().Be(FeedbackStatus.Resolved);
        updated.ResolvedAt.Should().NotBeNull();
        updated.ResolvedByUserId.Should().NotBeNull();
    }

    [Fact]
    public async Task UpdateStatusAsync_ClearsResolvedFields_WhenReopened()
    {
        var actorId = Guid.NewGuid();
        var report = await CreateTestReport();
        await _service.UpdateStatusAsync(report.Id, FeedbackStatus.Resolved, actorId);
        await _service.UpdateStatusAsync(report.Id, FeedbackStatus.Open, actorId);

        var updated = await _dbContext.FeedbackReports.FindAsync(report.Id);
        updated!.Status.Should().Be(FeedbackStatus.Open);
        updated.ResolvedAt.Should().BeNull();
        updated.ResolvedByUserId.Should().BeNull();
    }

    [Fact]
    public async Task GetFeedbackListAsync_FiltersByStatus()
    {
        await CreateTestReport(FeedbackStatus.Open);
        await CreateTestReport(FeedbackStatus.Open);
        await CreateTestReport(FeedbackStatus.Resolved);

        var results = await _service.GetFeedbackListAsync(status: FeedbackStatus.Open);

        results.Should().HaveCount(2);
    }

    [Fact]
    public async Task PostMessageAsync_AdminMessage_SetsLastAdminMessageAt_And_SendsEmail()
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

        var adminId = Guid.NewGuid();
        var message = await _service.PostMessageAsync(report.Id, adminId, "Looking into it", isAdmin: true);

        message.Content.Should().Be("Looking into it");
        message.SenderUserId.Should().Be(adminId);

        var updated = await _dbContext.FeedbackReports.FindAsync(report.Id);
        updated!.LastAdminMessageAt.Should().NotBeNull();
        updated.LastReporterMessageAt.Should().BeNull();

        await _emailService.Received(1).SendFeedbackResponseAsync(
            "reporter@test.com", "Reporter", "Test", "Looking into it",
            $"/Feedback/{report.Id}", Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
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
        var updated = await _dbContext.FeedbackReports.FindAsync(report.Id);
        updated!.LastReporterMessageAt.Should().NotBeNull();
        updated.LastAdminMessageAt.Should().BeNull();

        await _emailService.DidNotReceive().SendFeedbackResponseAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
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
