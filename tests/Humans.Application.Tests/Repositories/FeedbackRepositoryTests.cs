using AwesomeAssertions;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Testing;
using Xunit;

namespace Humans.Application.Tests.Repositories;

public sealed class FeedbackRepositoryTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly FeedbackRepository _repo;

    public FeedbackRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new HumansDbContext(options);
        _clock = new FakeClock(Instant.FromUtc(2026, 3, 18, 12, 0));
        _repo = new FeedbackRepository(_dbContext);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    // ==========================================================================
    // GetByIdAsync / FindForMutationAsync
    // ==========================================================================

    [Fact]
    public async Task GetByIdAsync_ReturnsNull_WhenNotFound()
    {
        var result = await _repo.GetByIdAsync(Guid.NewGuid());
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetByIdAsync_IncludesAggregateLocalMessagesOrderedByCreatedAt()
    {
        var reportId = Guid.NewGuid();
        var now = _clock.GetCurrentInstant();
        _dbContext.FeedbackReports.Add(new FeedbackReport
        {
            Id = reportId,
            UserId = Guid.NewGuid(),
            Category = FeedbackCategory.Bug,
            Description = "d",
            PageUrl = "/",
            Status = FeedbackStatus.Open,
            CreatedAt = now,
            UpdatedAt = now
        });
        await _dbContext.FeedbackMessages.AddRangeAsync(
            new FeedbackMessage { Id = Guid.NewGuid(), FeedbackReportId = reportId, Content = "second", CreatedAt = now + Duration.FromMinutes(5) },
            new FeedbackMessage { Id = Guid.NewGuid(), FeedbackReportId = reportId, Content = "first", CreatedAt = now + Duration.FromMinutes(1) });
        await _dbContext.SaveChangesAsync();

        var result = await _repo.GetByIdAsync(reportId);

        result.Should().NotBeNull();
        result!.Messages.Should().HaveCount(2);
        result.Messages.First().Content.Should().Be("first");
        result.Messages.Last().Content.Should().Be("second");
    }

    [Fact]
    public async Task FindForMutationAsync_ReturnsTrackedEntity()
    {
        var reportId = Guid.NewGuid();
        var now = _clock.GetCurrentInstant();
        _dbContext.FeedbackReports.Add(new FeedbackReport
        {
            Id = reportId,
            UserId = Guid.NewGuid(),
            Category = FeedbackCategory.Bug,
            Description = "d",
            PageUrl = "/",
            Status = FeedbackStatus.Open,
            CreatedAt = now,
            UpdatedAt = now
        });
        await _dbContext.SaveChangesAsync();

        var result = await _repo.FindForMutationAsync(reportId);

        result.Should().NotBeNull();
        // Mutate and save through the repository.
        result!.Status = FeedbackStatus.Resolved;
        await _repo.SaveTrackedReportAsync(result);

        var reloaded = await _dbContext.FeedbackReports.AsNoTracking()
            .FirstAsync(r => r.Id == reportId);
        reloaded.Status.Should().Be(FeedbackStatus.Resolved);
    }

    // ==========================================================================
    // GetListAsync filters
    // ==========================================================================

    [Fact]
    public async Task GetListAsync_FiltersByAssignedTeam_AndOrdersByCreatedAtDesc()
    {
        var teamA = Guid.NewGuid();
        var teamB = Guid.NewGuid();
        var user = Guid.NewGuid();
        var now = _clock.GetCurrentInstant();

        await _dbContext.FeedbackReports.AddRangeAsync(
            new FeedbackReport
            {
                Id = Guid.NewGuid(), UserId = user, Category = FeedbackCategory.Bug,
                Description = "a", PageUrl = "/a", Status = FeedbackStatus.Open,
                AssignedToTeamId = teamA, CreatedAt = now, UpdatedAt = now
            },
            new FeedbackReport
            {
                Id = Guid.NewGuid(), UserId = user, Category = FeedbackCategory.Bug,
                Description = "b", PageUrl = "/b", Status = FeedbackStatus.Open,
                AssignedToTeamId = teamB, CreatedAt = now + Duration.FromMinutes(1), UpdatedAt = now
            });
        await _dbContext.SaveChangesAsync();

        var filtered = await _repo.GetListAsync(
            status: null, category: null, reporterUserId: null,
            assignedToUserId: null, assignedToTeamId: teamA,
            unassignedOnly: null, limit: 50);

        filtered.Should().ContainSingle();
        filtered[0].AssignedToTeamId.Should().Be(teamA);
    }

    [Fact]
    public async Task GetListAsync_UnassignedOnly_ReturnsReportsWithNoAssignment()
    {
        var user = Guid.NewGuid();
        var now = _clock.GetCurrentInstant();

        await _dbContext.FeedbackReports.AddRangeAsync(
            new FeedbackReport
            {
                Id = Guid.NewGuid(), UserId = user, Category = FeedbackCategory.Bug,
                Description = "unassigned", PageUrl = "/", Status = FeedbackStatus.Open,
                CreatedAt = now, UpdatedAt = now
            },
            new FeedbackReport
            {
                Id = Guid.NewGuid(), UserId = user, Category = FeedbackCategory.Bug,
                Description = "assigned", PageUrl = "/", Status = FeedbackStatus.Open,
                AssignedToTeamId = Guid.NewGuid(), CreatedAt = now, UpdatedAt = now
            });
        await _dbContext.SaveChangesAsync();

        var result = await _repo.GetListAsync(
            status: null, category: null, reporterUserId: null,
            assignedToUserId: null, assignedToTeamId: null,
            unassignedOnly: true, limit: 50);

        result.Should().ContainSingle();
        result[0].Description.Should().Be("unassigned");
    }

    // ==========================================================================
    // GetActionableCountAsync
    // ==========================================================================

    [Fact]
    public async Task GetActionableCountAsync_CountsOpenWithoutAdminReplyAndReporterAfterAdmin()
    {
        var user = Guid.NewGuid();
        var now = _clock.GetCurrentInstant();

        await _dbContext.FeedbackReports.AddRangeAsync(
            new FeedbackReport  // Open, no admin reply -> actionable
            {
                Id = Guid.NewGuid(), UserId = user, Category = FeedbackCategory.Bug,
                Description = "a", PageUrl = "/a", Status = FeedbackStatus.Open,
                CreatedAt = now, UpdatedAt = now
            },
            new FeedbackReport  // Reporter replied after admin -> actionable
            {
                Id = Guid.NewGuid(), UserId = user, Category = FeedbackCategory.Bug,
                Description = "b", PageUrl = "/b", Status = FeedbackStatus.Acknowledged,
                CreatedAt = now, UpdatedAt = now,
                LastAdminMessageAt = now,
                LastReporterMessageAt = now + Duration.FromMinutes(5)
            },
            new FeedbackReport  // Resolved -> not actionable
            {
                Id = Guid.NewGuid(), UserId = user, Category = FeedbackCategory.Bug,
                Description = "c", PageUrl = "/c", Status = FeedbackStatus.Resolved,
                CreatedAt = now, UpdatedAt = now, ResolvedAt = now
            });
        await _dbContext.SaveChangesAsync();

        var count = await _repo.GetActionableCountAsync();
        count.Should().Be(2);
    }

    // ==========================================================================
    // GetReporterCountsAsync
    // ==========================================================================

    [Fact]
    public async Task GetReporterCountsAsync_GroupsByUserId_AndCounts()
    {
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        var now = _clock.GetCurrentInstant();
        await _dbContext.FeedbackReports.AddRangeAsync(
            new FeedbackReport { Id = Guid.NewGuid(), UserId = u1, Category = FeedbackCategory.Bug, Description = "a", PageUrl = "/", Status = FeedbackStatus.Open, CreatedAt = now, UpdatedAt = now },
            new FeedbackReport { Id = Guid.NewGuid(), UserId = u1, Category = FeedbackCategory.Bug, Description = "b", PageUrl = "/", Status = FeedbackStatus.Open, CreatedAt = now, UpdatedAt = now },
            new FeedbackReport { Id = Guid.NewGuid(), UserId = u2, Category = FeedbackCategory.Bug, Description = "c", PageUrl = "/", Status = FeedbackStatus.Open, CreatedAt = now, UpdatedAt = now });
        await _dbContext.SaveChangesAsync();

        var rows = await _repo.GetReporterCountsAsync();

        rows.Should().HaveCount(2);
        rows.Single(r => r.UserId == u1).Count.Should().Be(2);
        rows.Single(r => r.UserId == u2).Count.Should().Be(1);
    }

    // ==========================================================================
    // AddMessageAndSaveReportAsync
    // ==========================================================================

    [Fact]
    public async Task AddMessageAndSaveReportAsync_PersistsMessageAndUpdatesReport_InOneSaveChanges()
    {
        var reportId = Guid.NewGuid();
        var now = _clock.GetCurrentInstant();
        _dbContext.FeedbackReports.Add(new FeedbackReport
        {
            Id = reportId,
            UserId = Guid.NewGuid(),
            Category = FeedbackCategory.Bug,
            Description = "d",
            PageUrl = "/",
            Status = FeedbackStatus.Open,
            CreatedAt = now,
            UpdatedAt = now
        });
        await _dbContext.SaveChangesAsync();

        var tracked = await _repo.FindForMutationAsync(reportId);
        tracked.Should().NotBeNull();

        tracked!.LastAdminMessageAt = now + Duration.FromMinutes(1);
        tracked.UpdatedAt = now + Duration.FromMinutes(1);

        var msg = new FeedbackMessage
        {
            Id = Guid.NewGuid(),
            FeedbackReportId = reportId,
            Content = "hello",
            CreatedAt = now + Duration.FromMinutes(1)
        };

        await _repo.AddMessageAndSaveReportAsync(msg, tracked);

        var reloadedReport = await _dbContext.FeedbackReports.AsNoTracking().FirstAsync(r => r.Id == reportId);
        reloadedReport.LastAdminMessageAt.Should().Be(now + Duration.FromMinutes(1));

        var reloadedMsg = await _dbContext.FeedbackMessages.AsNoTracking().FirstAsync(m => m.Id == msg.Id);
        reloadedMsg.Content.Should().Be("hello");
    }

    // ==========================================================================
    // GetMessagesAsync
    // ==========================================================================

    [Fact]
    public async Task GetMessagesAsync_ReturnsMessagesOrderedByCreatedAtAscending()
    {
        var reportId = Guid.NewGuid();
        var now = _clock.GetCurrentInstant();
        _dbContext.FeedbackReports.Add(new FeedbackReport
        {
            Id = reportId, UserId = Guid.NewGuid(), Category = FeedbackCategory.Bug,
            Description = "d", PageUrl = "/", Status = FeedbackStatus.Open,
            CreatedAt = now, UpdatedAt = now
        });
        await _dbContext.FeedbackMessages.AddRangeAsync(
            new FeedbackMessage { Id = Guid.NewGuid(), FeedbackReportId = reportId, Content = "second", CreatedAt = now + Duration.FromMinutes(2) },
            new FeedbackMessage { Id = Guid.NewGuid(), FeedbackReportId = reportId, Content = "first", CreatedAt = now + Duration.FromMinutes(1) });
        await _dbContext.SaveChangesAsync();

        var result = await _repo.GetMessagesAsync(reportId);

        result.Should().HaveCount(2);
        result[0].Content.Should().Be("first");
        result[1].Content.Should().Be("second");
    }

    // ==========================================================================
    // GetForUserExportAsync
    // ==========================================================================

    [Fact]
    public async Task GetForUserExportAsync_ReturnsOnlyForUser_AndIncludesMessages()
    {
        var me = Guid.NewGuid();
        var other = Guid.NewGuid();
        var now = _clock.GetCurrentInstant();
        var myReport = new FeedbackReport
        {
            Id = Guid.NewGuid(), UserId = me, Category = FeedbackCategory.Bug,
            Description = "mine", PageUrl = "/", Status = FeedbackStatus.Open,
            CreatedAt = now, UpdatedAt = now
        };
        var otherReport = new FeedbackReport
        {
            Id = Guid.NewGuid(), UserId = other, Category = FeedbackCategory.Bug,
            Description = "theirs", PageUrl = "/", Status = FeedbackStatus.Open,
            CreatedAt = now, UpdatedAt = now
        };
        await _dbContext.FeedbackReports.AddRangeAsync(myReport, otherReport);
        _dbContext.FeedbackMessages.Add(new FeedbackMessage
        {
            Id = Guid.NewGuid(),
            FeedbackReportId = myReport.Id,
            Content = "note",
            CreatedAt = now
        });
        await _dbContext.SaveChangesAsync();

        var result = await _repo.GetForUserExportAsync(me);

        result.Should().ContainSingle();
        result[0].Description.Should().Be("mine");
        result[0].Messages.Should().ContainSingle();
    }
}
