using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Humans.Application.Interfaces;
using Humans.Domain;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Services;
using Xunit;
using MemberApplication = Humans.Domain.Entities.Application;

namespace Humans.Application.Tests.Services;

public class ApplicationDecisionServiceTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly ApplicationDecisionService _service;
    private readonly IAuditLogService _auditLogService = Substitute.For<IAuditLogService>();
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();
    private readonly INotificationService _notificationService = Substitute.For<INotificationService>();
    private readonly ISystemTeamSync _syncJob = Substitute.For<ISystemTeamSync>();
    private readonly IHumansMetrics _metrics = Substitute.For<IHumansMetrics>();
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());

    public ApplicationDecisionServiceTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new HumansDbContext(options);
        _clock = new FakeClock(Instant.FromUtc(2026, 3, 1, 12, 0));
        _service = new ApplicationDecisionService(
            _dbContext, _auditLogService, _emailService, _notificationService, _syncJob,
            _metrics, _clock, _cache,
            NullLogger<ApplicationDecisionService>.Instance);
    }

    public void Dispose()
    {
        _cache.Dispose();
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    // --- Submit flow ---

    [Fact]
    public async Task SubmitAsync_ValidColaborador_CreatesApplication()
    {
        var userId = Guid.NewGuid();

        var result = await _service.SubmitAsync(
            userId, MembershipTier.Colaborador, "I want to contribute",
            "Extra info", null, null, "en");

        result.Success.Should().BeTrue();
        result.ApplicationId.Should().NotBeNull();
        var app = await _dbContext.Applications.FirstAsync();
        app.MembershipTier.Should().Be(MembershipTier.Colaborador);
        app.Motivation.Should().Be("I want to contribute");
        app.Status.Should().Be(ApplicationStatus.Submitted);
    }

    [Fact]
    public async Task SubmitAsync_Asociado_IncludesExtraFields()
    {
        var userId = Guid.NewGuid();

        await _service.SubmitAsync(
            userId, MembershipTier.Asociado, "Motivation",
            null, "My contribution", "I understand the role", "es");

        var app = await _dbContext.Applications.FirstAsync();
        app.SignificantContribution.Should().Be("My contribution");
        app.RoleUnderstanding.Should().Be("I understand the role");
    }

    [Fact]
    public async Task SubmitAsync_Colaborador_ExcludesAsociadoFields()
    {
        var userId = Guid.NewGuid();

        await _service.SubmitAsync(
            userId, MembershipTier.Colaborador, "Motivation",
            null, "Should be ignored", "Also ignored", "en");

        var app = await _dbContext.Applications.FirstAsync();
        app.SignificantContribution.Should().BeNull();
        app.RoleUnderstanding.Should().BeNull();
    }

    [Fact]
    public async Task SubmitAsync_AlreadyPending_ReturnsError()
    {
        var userId = Guid.NewGuid();
        await SeedSubmittedApplicationAsync(userId);

        var result = await _service.SubmitAsync(
            userId, MembershipTier.Colaborador, "Motivation",
            null, null, null, "en");

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("AlreadyPending");
    }

    [Fact]
    public async Task SubmitAsync_ReturnsApplicationId()
    {
        var result = await _service.SubmitAsync(
            Guid.NewGuid(), MembershipTier.Colaborador, "Motivation",
            null, null, null, "en");

        result.ApplicationId.Should().NotBeNull();
        var app = await _dbContext.Applications.FirstAsync();
        result.ApplicationId.Should().Be(app.Id);
    }

    // --- Withdraw flow ---

    [Fact]
    public async Task WithdrawAsync_SubmittedApplication_SetsWithdrawn()
    {
        var userId = Guid.NewGuid();
        var app = await SeedSubmittedApplicationAsync(userId);

        var result = await _service.WithdrawAsync(app.Id, userId);

        result.Success.Should().BeTrue();
        var updated = await _dbContext.Applications.FirstAsync(a => a.Id == app.Id);
        updated.Status.Should().Be(ApplicationStatus.Withdrawn);
        _metrics.Received().RecordApplicationProcessed("withdrawn");
    }

    [Fact]
    public async Task WithdrawAsync_NotSubmitted_ReturnsError()
    {
        var userId = Guid.NewGuid();
        var app = await SeedSubmittedApplicationAsync(userId);
        app.Withdraw(_clock);
        await _dbContext.SaveChangesAsync();

        var result = await _service.WithdrawAsync(app.Id, userId);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("CannotWithdraw");
    }

    [Fact]
    public async Task WithdrawAsync_WrongUser_ReturnsNotFound()
    {
        var userId = Guid.NewGuid();
        var app = await SeedSubmittedApplicationAsync(userId);

        var result = await _service.WithdrawAsync(app.Id, Guid.NewGuid());

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("NotFound");
    }

    // --- Approve flow ---

    [Fact]
    public async Task ApproveAsync_SubmittedApplication_SetsApproved()
    {
        var (app, _) = await SeedApplicationWithUserProfileAsync(MembershipTier.Colaborador);

        var result = await _service.ApproveAsync(app.Id, Guid.NewGuid(), "Approved", null);

        result.Success.Should().BeTrue();
        var updated = await _dbContext.Applications.FirstAsync(a => a.Id == app.Id);
        updated.Status.Should().Be(ApplicationStatus.Approved);
    }

    [Fact]
    public async Task ApproveAsync_SetsTermExpiry()
    {
        var (app, _) = await SeedApplicationWithUserProfileAsync(MembershipTier.Colaborador);

        await _service.ApproveAsync(app.Id, Guid.NewGuid(), null, null);

        var updated = await _dbContext.Applications.FirstAsync(a => a.Id == app.Id);
        var today = _clock.GetCurrentInstant().InUtc().Date;
        var expectedExpiry = TermExpiryCalculator.ComputeTermExpiry(today);
        updated.TermExpiresAt.Should().Be(expectedExpiry);
    }

    [Fact]
    public async Task ApproveAsync_UpdatesProfileTier()
    {
        var (app, userId) = await SeedApplicationWithUserProfileAsync(MembershipTier.Asociado);

        await _service.ApproveAsync(app.Id, Guid.NewGuid(), null, null);

        var profile = await _dbContext.Profiles.FirstAsync(p => p.UserId == userId);
        profile.MembershipTier.Should().Be(MembershipTier.Asociado);
    }

    [Fact]
    public async Task ApproveAsync_DeletesBoardVotes()
    {
        var (app, _) = await SeedApplicationWithUserProfileAsync(MembershipTier.Colaborador);
        _dbContext.BoardVotes.Add(new BoardVote
        {
            Id = Guid.NewGuid(),
            ApplicationId = app.Id,
            BoardMemberUserId = Guid.NewGuid(),
            Vote = VoteChoice.Yay,
            VotedAt = _clock.GetCurrentInstant()
        });
        await _dbContext.SaveChangesAsync();

        await _service.ApproveAsync(app.Id, Guid.NewGuid(), null, null);

        var votes = await _dbContext.BoardVotes.Where(v => v.ApplicationId == app.Id).ToListAsync();
        votes.Should().BeEmpty();
    }

    [Fact]
    public async Task ApproveAsync_SyncsColaboradorTeam()
    {
        var (app, userId) = await SeedApplicationWithUserProfileAsync(MembershipTier.Colaborador);

        await _service.ApproveAsync(app.Id, Guid.NewGuid(), null, null);

        await _syncJob.Received().SyncColaboradorsMembershipForUserAsync(userId, Arg.Any<CancellationToken>());
        await _syncJob.DidNotReceive().SyncAsociadosMembershipForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApproveAsync_SyncsAsociadoTeam()
    {
        var (app, userId) = await SeedApplicationWithUserProfileAsync(MembershipTier.Asociado);

        await _service.ApproveAsync(app.Id, Guid.NewGuid(), null, null);

        await _syncJob.Received().SyncAsociadosMembershipForUserAsync(userId, Arg.Any<CancellationToken>());
        await _syncJob.DidNotReceive().SyncColaboradorsMembershipForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApproveAsync_NotSubmitted_ReturnsError()
    {
        var (app, _) = await SeedApplicationWithUserProfileAsync(MembershipTier.Colaborador);
        app.Withdraw(_clock);
        await _dbContext.SaveChangesAsync();

        var result = await _service.ApproveAsync(app.Id, Guid.NewGuid(), null, null);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("NotSubmitted");
    }

    [Fact]
    public async Task ApproveAsync_NotFound_ReturnsError()
    {
        var result = await _service.ApproveAsync(Guid.NewGuid(), Guid.NewGuid(), null, null);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("NotFound");
    }

    // --- Reject flow ---

    [Fact]
    public async Task RejectAsync_SubmittedApplication_SetsRejected()
    {
        var (app, _) = await SeedApplicationWithUserProfileAsync(MembershipTier.Colaborador);

        var result = await _service.RejectAsync(app.Id, Guid.NewGuid(), "Not ready", null);

        result.Success.Should().BeTrue();
        var updated = await _dbContext.Applications.FirstAsync(a => a.Id == app.Id);
        updated.Status.Should().Be(ApplicationStatus.Rejected);
        updated.DecisionNote.Should().Be("Not ready");
    }

    [Fact]
    public async Task RejectAsync_DeletesBoardVotes()
    {
        var (app, _) = await SeedApplicationWithUserProfileAsync(MembershipTier.Colaborador);
        _dbContext.BoardVotes.Add(new BoardVote
        {
            Id = Guid.NewGuid(),
            ApplicationId = app.Id,
            BoardMemberUserId = Guid.NewGuid(),
            Vote = VoteChoice.No,
            VotedAt = _clock.GetCurrentInstant()
        });
        await _dbContext.SaveChangesAsync();

        await _service.RejectAsync(app.Id, Guid.NewGuid(), "reason", null);

        var votes = await _dbContext.BoardVotes.Where(v => v.ApplicationId == app.Id).ToListAsync();
        votes.Should().BeEmpty();
    }

    [Fact]
    public async Task RejectAsync_DoesNotUpdateProfileTier()
    {
        var (app, userId) = await SeedApplicationWithUserProfileAsync(MembershipTier.Asociado);

        await _service.RejectAsync(app.Id, Guid.NewGuid(), "reason", null);

        var profile = await _dbContext.Profiles.FirstAsync(p => p.UserId == userId);
        profile.MembershipTier.Should().Be(MembershipTier.Volunteer);
    }

    [Fact]
    public async Task RejectAsync_DoesNotSyncTeams()
    {
        var (app, _) = await SeedApplicationWithUserProfileAsync(MembershipTier.Colaborador);

        await _service.RejectAsync(app.Id, Guid.NewGuid(), "reason", null);

        await _syncJob.DidNotReceive().SyncColaboradorsMembershipForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _syncJob.DidNotReceive().SyncAsociadosMembershipForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RejectAsync_NotSubmitted_ReturnsError()
    {
        var (app, _) = await SeedApplicationWithUserProfileAsync(MembershipTier.Colaborador);
        app.Withdraw(_clock);
        await _dbContext.SaveChangesAsync();

        var result = await _service.RejectAsync(app.Id, Guid.NewGuid(), "reason", null);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("NotSubmitted");
    }

    // --- GetUserApplicationsAsync ---

    [Fact]
    public async Task GetUserApplicationsAsync_ReturnsAllStatusesOrderedBySubmittedAtDesc()
    {
        var userId = Guid.NewGuid();
        var app1 = new MemberApplication
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            MembershipTier = MembershipTier.Colaborador,
            Motivation = "M1",
            SubmittedAt = _clock.GetCurrentInstant() - Duration.FromDays(2),
            UpdatedAt = _clock.GetCurrentInstant()
        };
        var app2 = new MemberApplication
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            MembershipTier = MembershipTier.Asociado,
            Motivation = "M2",
            SubmittedAt = _clock.GetCurrentInstant() - Duration.FromDays(1),
            UpdatedAt = _clock.GetCurrentInstant()
        };
        var app3 = new MemberApplication
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            MembershipTier = MembershipTier.Colaborador,
            Motivation = "M3",
            SubmittedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        };
        await _dbContext.Applications.AddRangeAsync(app1, app2, app3);
        // Approve one, withdraw another, leave the third as Submitted
        app1.Approve(Guid.NewGuid(), "ok", _clock);
        app2.Withdraw(_clock);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetUserApplicationsAsync(userId);

        result.Should().HaveCount(3);
        result[0].Id.Should().Be(app3.Id); // most recent
        result[1].Id.Should().Be(app2.Id);
        result[2].Id.Should().Be(app1.Id); // oldest
        result.Select(a => a.Status).Should().Contain(ApplicationStatus.Submitted);
        result.Select(a => a.Status).Should().Contain(ApplicationStatus.Approved);
        result.Select(a => a.Status).Should().Contain(ApplicationStatus.Withdrawn);
    }

    [Fact]
    public async Task GetUserApplicationsAsync_ExcludesOtherUsers()
    {
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        await SeedSubmittedApplicationAsync(userA);
        await SeedSubmittedApplicationAsync(userB);

        var result = await _service.GetUserApplicationsAsync(userA);

        result.Should().HaveCount(1);
        result[0].UserId.Should().Be(userA);
    }

    [Fact]
    public async Task GetUserApplicationsAsync_EmptyForNoApps()
    {
        var result = await _service.GetUserApplicationsAsync(Guid.NewGuid());

        result.Should().BeEmpty();
    }

    // --- GetUserApplicationDetailAsync ---

    [Fact]
    public async Task GetUserApplicationDetailAsync_ReturnsApplicationWithIncludes()
    {
        var reviewerId = Guid.NewGuid();
        _dbContext.Users.Add(new User
        {
            Id = reviewerId,
            DisplayName = "Reviewer",
            UserName = "reviewer@test.com",
            Email = "reviewer@test.com"
        });
        await _dbContext.SaveChangesAsync();

        var (app, userId) = await SeedApplicationWithUserProfileAsync(MembershipTier.Colaborador);
        app.Approve(reviewerId, "Good", _clock);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetUserApplicationDetailAsync(app.Id, userId);

        result.Should().NotBeNull();
        result!.ReviewedByUser.Should().NotBeNull();
        result.ReviewedByUser!.DisplayName.Should().Be("Reviewer");
        result.StateHistory.Should().NotBeEmpty();
    }

    [Fact]
    public async Task GetUserApplicationDetailAsync_WrongUser_ReturnsNull()
    {
        var userId = Guid.NewGuid();
        var app = await SeedSubmittedApplicationAsync(userId);

        var result = await _service.GetUserApplicationDetailAsync(app.Id, Guid.NewGuid());

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetUserApplicationDetailAsync_NonExistent_ReturnsNull()
    {
        var result = await _service.GetUserApplicationDetailAsync(Guid.NewGuid(), Guid.NewGuid());

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetUserApplicationDetailAsync_IncludesStateHistory()
    {
        var (app, userId) = await SeedApplicationWithUserProfileAsync(MembershipTier.Colaborador);
        app.Withdraw(_clock);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetUserApplicationDetailAsync(app.Id, userId);

        result.Should().NotBeNull();
        result!.StateHistory.Should().HaveCount(1);
        result.StateHistory.First().Status.Should().Be(ApplicationStatus.Withdrawn);
    }

    // --- GetFilteredApplicationsAsync ---

    [Fact]
    public async Task GetFilteredApplicationsAsync_DefaultsToSubmitted()
    {
        var (submittedApp, _) = await SeedApplicationWithUserProfileAsync(MembershipTier.Colaborador);
        var (approvedApp, _) = await SeedApplicationWithUserProfileAsync(MembershipTier.Colaborador);
        approvedApp.Approve(Guid.NewGuid(), "ok", _clock);
        await _dbContext.SaveChangesAsync();

        var (items, totalCount) = await _service.GetFilteredApplicationsAsync(null, null, 1, 10);

        totalCount.Should().Be(1);
        items.Should().HaveCount(1);
        items[0].Id.Should().Be(submittedApp.Id);
        items[0].Status.Should().Be(ApplicationStatus.Submitted);
    }

    [Fact]
    public async Task GetFilteredApplicationsAsync_FiltersByStatus()
    {
        var (_, _) = await SeedApplicationWithUserProfileAsync(MembershipTier.Colaborador);
        var (approvedApp, _) = await SeedApplicationWithUserProfileAsync(MembershipTier.Colaborador);
        approvedApp.Approve(Guid.NewGuid(), "ok", _clock);
        await _dbContext.SaveChangesAsync();

        var (items, totalCount) = await _service.GetFilteredApplicationsAsync("Approved", null, 1, 10);

        totalCount.Should().Be(1);
        items.Should().HaveCount(1);
        items[0].Id.Should().Be(approvedApp.Id);
    }

    [Fact]
    public async Task GetFilteredApplicationsAsync_FiltersByTier()
    {
        var (colabApp, _) = await SeedApplicationWithUserProfileAsync(MembershipTier.Colaborador);
        var (asociadoApp, _) = await SeedApplicationWithUserProfileAsync(MembershipTier.Asociado);

        var (items, totalCount) = await _service.GetFilteredApplicationsAsync(null, "Colaborador", 1, 10);

        totalCount.Should().Be(1);
        items.Should().HaveCount(1);
        items[0].MembershipTier.Should().Be(MembershipTier.Colaborador);
    }

    [Fact]
    public async Task GetFilteredApplicationsAsync_Pagination()
    {
        // Seed 3 Submitted apps
        for (var i = 0; i < 3; i++)
            await SeedApplicationWithUserProfileAsync(MembershipTier.Colaborador);

        var (items, totalCount) = await _service.GetFilteredApplicationsAsync(null, null, 1, 2);

        totalCount.Should().Be(3);
        items.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetFilteredApplicationsAsync_CombinedFilter()
    {
        // Submitted Colaborador
        var (_, _) = await SeedApplicationWithUserProfileAsync(MembershipTier.Colaborador);
        // Submitted Asociado
        var (_, _) = await SeedApplicationWithUserProfileAsync(MembershipTier.Asociado);
        // Approved Colaborador
        var (approvedColab, _) = await SeedApplicationWithUserProfileAsync(MembershipTier.Colaborador);
        approvedColab.Approve(Guid.NewGuid(), "ok", _clock);
        await _dbContext.SaveChangesAsync();

        var (items, totalCount) = await _service.GetFilteredApplicationsAsync("Submitted", "Asociado", 1, 10);

        totalCount.Should().Be(1);
        items.Should().HaveCount(1);
        items[0].MembershipTier.Should().Be(MembershipTier.Asociado);
        items[0].Status.Should().Be(ApplicationStatus.Submitted);
    }

    // --- GetApplicationDetailAsync ---

    [Fact]
    public async Task GetApplicationDetailAsync_ReturnsApplicationWithIncludes()
    {
        var reviewerId = Guid.NewGuid();
        _dbContext.Users.Add(new User
        {
            Id = reviewerId,
            DisplayName = "Admin",
            UserName = "admin@test.com",
            Email = "admin@test.com"
        });
        await _dbContext.SaveChangesAsync();

        var (app, _) = await SeedApplicationWithUserProfileAsync(MembershipTier.Colaborador);
        app.Approve(reviewerId, "Looks good", _clock);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetApplicationDetailAsync(app.Id);

        result.Should().NotBeNull();
        result!.User.Should().NotBeNull();
        result.ReviewedByUser.Should().NotBeNull();
        result.ReviewedByUser!.DisplayName.Should().Be("Admin");
        result.StateHistory.Should().NotBeEmpty();
    }

    [Fact]
    public async Task GetApplicationDetailAsync_NonExistent_ReturnsNull()
    {
        var result = await _service.GetApplicationDetailAsync(Guid.NewGuid());

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetApplicationDetailAsync_NoOwnershipFilter()
    {
        var (app, userId) = await SeedApplicationWithUserProfileAsync(MembershipTier.Asociado);

        // Admin detail query works without knowing the userId
        var result = await _service.GetApplicationDetailAsync(app.Id);

        result.Should().NotBeNull();
        result!.Id.Should().Be(app.Id);
        result.UserId.Should().Be(userId);
        result.User.Should().NotBeNull();
    }

    // --- Helpers ---

    private async Task<MemberApplication> SeedSubmittedApplicationAsync(Guid userId)
    {
        var app = new MemberApplication
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            MembershipTier = MembershipTier.Colaborador,
            Motivation = "Motivation",
            SubmittedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        };
        _dbContext.Applications.Add(app);
        await _dbContext.SaveChangesAsync();
        return app;
    }

    private async Task<(MemberApplication App, Guid UserId)> SeedApplicationWithUserProfileAsync(
        MembershipTier tier)
    {
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            DisplayName = "Applicant",
            UserName = $"applicant-{userId}@test.com",
            Email = $"applicant-{userId}@test.com",
            PreferredLanguage = "en"
        };
        _dbContext.Users.Add(user);
        _dbContext.Profiles.Add(new Profile
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            BurnerName = "Applicant",
            FirstName = "Test",
            LastName = "User",
            MembershipTier = MembershipTier.Volunteer,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        });
        var app = new MemberApplication
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            MembershipTier = tier,
            Motivation = "Motivation",
            SubmittedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        };
        _dbContext.Applications.Add(app);
        await _dbContext.SaveChangesAsync();
        return (app, userId);
    }
}
