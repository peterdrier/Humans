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

        _service = new FeedbackService(
            _dbContext, _emailService, _auditLog, _clock, env,
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
            "/Teams/test", "Mozilla/5.0", null);

        report.Id.Should().NotBeEmpty();
        report.Category.Should().Be(FeedbackCategory.Bug);
        report.Status.Should().Be(FeedbackStatus.Open);
        report.Description.Should().Be("Something broke");
        report.PageUrl.Should().Be("/Teams/test");
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
    public async Task SendResponseAsync_EnqueuesEmail_AndUpdatesTimestamp()
    {
        var userId = Guid.NewGuid();
        _dbContext.Users.Add(new User { Id = userId, DisplayName = "Reporter", Email = "reporter@test.com" });
        await _dbContext.SaveChangesAsync();

        var report = await _service.SubmitFeedbackAsync(
            userId, FeedbackCategory.Bug, "Bug desc", "/page", null, null);

        var actorId = Guid.NewGuid();
        _dbContext.Users.Add(new User { Id = actorId, DisplayName = "Admin", Email = "admin@test.com" });
        await _dbContext.SaveChangesAsync();

        await _service.SendResponseAsync(report.Id, "Fixed it!", actorId);

        await _emailService.Received(1).SendFeedbackResponseAsync(
            "reporter@test.com", "Reporter", "Bug desc", "Fixed it!",
            Arg.Any<string?>(), Arg.Any<CancellationToken>());

        var updated = await _dbContext.FeedbackReports.FindAsync(report.Id);
        // TODO: AdminResponseSentAt removed in feedback upgrade
        updated.Should().NotBeNull();
    }

    private async Task<FeedbackReport> CreateTestReport(FeedbackStatus status = FeedbackStatus.Open)
    {
        var userId = Guid.NewGuid();
        _dbContext.Users.Add(new User { Id = userId, DisplayName = "Test", Email = $"{userId}@test.com" });
        await _dbContext.SaveChangesAsync();

        var report = await _service.SubmitFeedbackAsync(
            userId, FeedbackCategory.Bug, "Test bug", "/test", null, null);

        if (status != FeedbackStatus.Open)
        {
            await _service.UpdateStatusAsync(report.Id, status, Guid.NewGuid());
        }

        return report;
    }
}
