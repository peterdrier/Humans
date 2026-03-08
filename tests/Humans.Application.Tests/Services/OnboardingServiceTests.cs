using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Services;
using Xunit;
using MemberApplication = Humans.Domain.Entities.Application;

namespace Humans.Application.Tests.Services;

public class OnboardingServiceTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly OnboardingService _service;
    private readonly IAuditLogService _auditLogService = Substitute.For<IAuditLogService>();
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();
    private readonly ISystemTeamSync _syncJob = Substitute.For<ISystemTeamSync>();
    private readonly IMembershipCalculator _membershipCalculator = Substitute.For<IMembershipCalculator>();
    private readonly IHumansMetrics _metrics = Substitute.For<IHumansMetrics>();
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());

    public OnboardingServiceTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new HumansDbContext(options);
        _clock = new FakeClock(Instant.FromUtc(2026, 3, 1, 12, 0));
        _service = new OnboardingService(
            _dbContext, _auditLogService, _emailService, _syncJob,
            _membershipCalculator, _metrics, _clock, _cache,
            NullLogger<OnboardingService>.Instance);
    }

    public void Dispose()
    {
        _cache.Dispose();
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    // --- Consent Coordinator flow ---

    [Fact]
    public async Task ClearConsentCheckAsync_ValidPending_SetsStatusToClearedAndApproves()
    {
        var userId = Guid.NewGuid();
        var reviewerId = Guid.NewGuid();
        await SeedProfileAsync(userId, consentCheckStatus: ConsentCheckStatus.Pending);
        _membershipCalculator.HasAllRequiredConsentsAsync(userId, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _service.ClearConsentCheckAsync(userId, reviewerId, "Reviewer", "All good");

        result.Success.Should().BeTrue();
        var profile = await _dbContext.Profiles.FirstAsync(p => p.UserId == userId);
        profile.ConsentCheckStatus.Should().Be(ConsentCheckStatus.Cleared);
        profile.IsApproved.Should().BeTrue();
        profile.ConsentCheckedByUserId.Should().Be(reviewerId);
        await _syncJob.Received().SyncVolunteersMembershipForUserAsync(userId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ClearConsentCheckAsync_ProfileNotFound_ReturnsNotFound()
    {
        var result = await _service.ClearConsentCheckAsync(Guid.NewGuid(), Guid.NewGuid(), "Reviewer", null);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("NotFound");
    }

    [Fact]
    public async Task ClearConsentCheckAsync_AlreadyRejected_ReturnsError()
    {
        var userId = Guid.NewGuid();
        await SeedProfileAsync(userId, rejectedAt: _clock.GetCurrentInstant());

        var result = await _service.ClearConsentCheckAsync(userId, Guid.NewGuid(), "Reviewer", null);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("AlreadyRejected");
    }

    [Fact]
    public async Task ClearConsentCheckAsync_MissingConsents_ReturnsError()
    {
        var userId = Guid.NewGuid();
        await SeedProfileAsync(userId, consentCheckStatus: ConsentCheckStatus.Pending);
        _membershipCalculator.HasAllRequiredConsentsAsync(userId, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _service.ClearConsentCheckAsync(userId, Guid.NewGuid(), "Reviewer", null);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("ConsentsRequired");
    }

    [Fact]
    public async Task FlagConsentCheckAsync_ValidProfile_SetsStatusToFlagged()
    {
        var userId = Guid.NewGuid();
        await SeedProfileAsync(userId, consentCheckStatus: ConsentCheckStatus.Pending);

        var result = await _service.FlagConsentCheckAsync(userId, Guid.NewGuid(), "Reviewer", "Concern");

        result.Success.Should().BeTrue();
        var profile = await _dbContext.Profiles.FirstAsync(p => p.UserId == userId);
        profile.ConsentCheckStatus.Should().Be(ConsentCheckStatus.Flagged);
        profile.IsApproved.Should().BeFalse();
        await _syncJob.Received().SyncVolunteersMembershipForUserAsync(userId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FlagConsentCheckAsync_ProfileNotFound_ReturnsNotFound()
    {
        var result = await _service.FlagConsentCheckAsync(Guid.NewGuid(), Guid.NewGuid(), "Reviewer", null);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("NotFound");
    }

    // --- Admin flow ---

    [Fact]
    public async Task ApproveVolunteerAsync_ValidPending_SetsIsApproved()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId, isApproved: false);

        var result = await _service.ApproveVolunteerAsync(userId, Guid.NewGuid(), "Admin");

        result.Success.Should().BeTrue();
        var profile = await _dbContext.Profiles.FirstAsync(p => p.UserId == userId);
        profile.IsApproved.Should().BeTrue();
        _metrics.Received().RecordVolunteerApproved();
    }

    [Fact]
    public async Task ApproveVolunteerAsync_NoProfile_ReturnsNotFound()
    {
        var userId = Guid.NewGuid();
        _dbContext.Users.Add(new User { Id = userId, DisplayName = "Test", UserName = "test@test.com", Email = "test@test.com" });
        await _dbContext.SaveChangesAsync();

        var result = await _service.ApproveVolunteerAsync(userId, Guid.NewGuid(), "Admin");

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("NotFound");
    }

    [Fact]
    public async Task SuspendAsync_ValidUser_SetsSuspended()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId);

        var result = await _service.SuspendAsync(userId, Guid.NewGuid(), "Admin", "Policy violation");

        result.Success.Should().BeTrue();
        var profile = await _dbContext.Profiles.FirstAsync(p => p.UserId == userId);
        profile.IsSuspended.Should().BeTrue();
        profile.AdminNotes.Should().Be("Policy violation");
        _metrics.Received().RecordMemberSuspended("admin");
    }

    [Fact]
    public async Task SuspendAsync_NoProfile_ReturnsNotFound()
    {
        var result = await _service.SuspendAsync(Guid.NewGuid(), Guid.NewGuid(), "Admin", null);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("NotFound");
    }

    [Fact]
    public async Task UnsuspendAsync_SuspendedUser_ClearsSuspension()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId, isSuspended: true);

        var result = await _service.UnsuspendAsync(userId, Guid.NewGuid(), "Admin");

        result.Success.Should().BeTrue();
        var profile = await _dbContext.Profiles.FirstAsync(p => p.UserId == userId);
        profile.IsSuspended.Should().BeFalse();
    }

    [Fact]
    public async Task UnsuspendAsync_NoProfile_ReturnsNotFound()
    {
        var result = await _service.UnsuspendAsync(Guid.NewGuid(), Guid.NewGuid(), "Admin");

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("NotFound");
    }

    // --- Signup rejection ---

    [Fact]
    public async Task RejectSignupAsync_ValidProfile_SetsRejected()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId);

        var result = await _service.RejectSignupAsync(userId, Guid.NewGuid(), "Reviewer", "Incomplete");

        result.Success.Should().BeTrue();
        var profile = await _dbContext.Profiles.FirstAsync(p => p.UserId == userId);
        profile.RejectedAt.Should().NotBeNull();
        profile.RejectionReason.Should().Be("Incomplete");
        profile.IsApproved.Should().BeFalse();
    }

    [Fact]
    public async Task RejectSignupAsync_AlreadyRejected_ReturnsError()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId, rejectedAt: _clock.GetCurrentInstant());

        var result = await _service.RejectSignupAsync(userId, Guid.NewGuid(), "Reviewer", "reason");

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("AlreadyRejected");
    }

    [Fact]
    public async Task RejectSignupAsync_ProfileNotFound_ReturnsNotFound()
    {
        var result = await _service.RejectSignupAsync(Guid.NewGuid(), Guid.NewGuid(), "Reviewer", null);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("NotFound");
    }

    [Fact]
    public async Task RejectSignupAsync_SendsRejectionEmail()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId);

        await _service.RejectSignupAsync(userId, Guid.NewGuid(), "Reviewer", "Incomplete");

        await _emailService.Received().SendSignupRejectedAsync(
            Arg.Any<string>(), Arg.Any<string>(), "Incomplete", Arg.Any<string>());
    }

    [Fact]
    public async Task RejectSignupAsync_DeprovisionsSyncTeams()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId);

        await _service.RejectSignupAsync(userId, Guid.NewGuid(), "Reviewer", "reason");

        await _syncJob.Received().SyncVolunteersMembershipForUserAsync(userId, Arg.Any<CancellationToken>());
        await _syncJob.Received().SyncColaboradorsMembershipForUserAsync(userId, Arg.Any<CancellationToken>());
        await _syncJob.Received().SyncAsociadosMembershipForUserAsync(userId, Arg.Any<CancellationToken>());
    }

    // --- Consent-check pending ---

    [Fact]
    public async Task SetConsentCheckPendingIfEligibleAsync_AllConsents_SetsPending()
    {
        var userId = Guid.NewGuid();
        await SeedProfileAsync(userId, isApproved: false, consentCheckStatus: null);
        _membershipCalculator.HasAllRequiredConsentsForTeamAsync(userId, Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _service.SetConsentCheckPendingIfEligibleAsync(userId);

        result.Should().BeTrue();
        var profile = await _dbContext.Profiles.FirstAsync(p => p.UserId == userId);
        profile.ConsentCheckStatus.Should().Be(ConsentCheckStatus.Pending);
    }

    [Fact]
    public async Task SetConsentCheckPendingIfEligibleAsync_AlreadyApproved_ReturnsFalse()
    {
        var userId = Guid.NewGuid();
        await SeedProfileAsync(userId, isApproved: true);

        var result = await _service.SetConsentCheckPendingIfEligibleAsync(userId);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task SetConsentCheckPendingIfEligibleAsync_AlreadyHasStatus_ReturnsFalse()
    {
        var userId = Guid.NewGuid();
        await SeedProfileAsync(userId, isApproved: false, consentCheckStatus: ConsentCheckStatus.Flagged);

        var result = await _service.SetConsentCheckPendingIfEligibleAsync(userId);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task SetConsentCheckPendingIfEligibleAsync_MissingConsents_ReturnsFalse()
    {
        var userId = Guid.NewGuid();
        await SeedProfileAsync(userId, isApproved: false, consentCheckStatus: null);
        _membershipCalculator.HasAllRequiredConsentsForTeamAsync(userId, Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _service.SetConsentCheckPendingIfEligibleAsync(userId);

        result.Should().BeFalse();
    }

    // --- Board vote ---

    [Fact]
    public async Task CastBoardVoteAsync_NewVote_CreatesVoteRecord()
    {
        var applicationId = Guid.NewGuid();
        var boardMemberId = Guid.NewGuid();
        await SeedApplicationAsync(applicationId);

        var result = await _service.CastBoardVoteAsync(applicationId, boardMemberId, VoteChoice.Yay, "Good candidate");

        result.Success.Should().BeTrue();
        var vote = await _dbContext.BoardVotes.FirstAsync();
        vote.ApplicationId.Should().Be(applicationId);
        vote.BoardMemberUserId.Should().Be(boardMemberId);
        vote.Vote.Should().Be(VoteChoice.Yay);
        vote.Note.Should().Be("Good candidate");
    }

    [Fact]
    public async Task CastBoardVoteAsync_ExistingVote_UpdatesVote()
    {
        var applicationId = Guid.NewGuid();
        var boardMemberId = Guid.NewGuid();
        await SeedApplicationAsync(applicationId);
        _dbContext.BoardVotes.Add(new BoardVote
        {
            Id = Guid.NewGuid(),
            ApplicationId = applicationId,
            BoardMemberUserId = boardMemberId,
            Vote = VoteChoice.Maybe,
            VotedAt = _clock.GetCurrentInstant()
        });
        await _dbContext.SaveChangesAsync();

        var result = await _service.CastBoardVoteAsync(applicationId, boardMemberId, VoteChoice.Yay, "Changed mind");

        result.Success.Should().BeTrue();
        var votes = await _dbContext.BoardVotes.Where(v => v.ApplicationId == applicationId).ToListAsync();
        votes.Should().HaveCount(1);
        votes[0].Vote.Should().Be(VoteChoice.Yay);
    }

    [Fact]
    public async Task CastBoardVoteAsync_ApplicationNotFound_ReturnsNotFound()
    {
        var result = await _service.CastBoardVoteAsync(Guid.NewGuid(), Guid.NewGuid(), VoteChoice.Yay, null);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("NotFound");
    }

    [Fact]
    public async Task CastBoardVoteAsync_ApplicationNotSubmitted_ReturnsError()
    {
        var applicationId = Guid.NewGuid();
        await SeedApplicationAsync(applicationId, status: ApplicationStatus.Approved);

        var result = await _service.CastBoardVoteAsync(applicationId, Guid.NewGuid(), VoteChoice.Yay, null);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("NotSubmitted");
    }

    // --- Helpers ---

    private async Task SeedProfileAsync(Guid userId,
        bool isApproved = false, bool isSuspended = false,
        ConsentCheckStatus? consentCheckStatus = null,
        Instant? rejectedAt = null)
    {
        _dbContext.Profiles.Add(new Profile
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            BurnerName = "Test",
            FirstName = "Test",
            LastName = "User",
            IsApproved = isApproved,
            IsSuspended = isSuspended,
            ConsentCheckStatus = consentCheckStatus,
            RejectedAt = rejectedAt,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        });
        await _dbContext.SaveChangesAsync();
    }

    private async Task SeedUserWithProfileAsync(Guid userId,
        bool isApproved = false, bool isSuspended = false,
        Instant? rejectedAt = null)
    {
        var user = new User
        {
            Id = userId,
            DisplayName = "Test User",
            UserName = $"test-{userId}@test.com",
            Email = $"test-{userId}@test.com",
            PreferredLanguage = "en"
        };
        _dbContext.Users.Add(user);
        _dbContext.Profiles.Add(new Profile
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            BurnerName = "Test",
            FirstName = "Test",
            LastName = "User",
            IsApproved = isApproved,
            IsSuspended = isSuspended,
            RejectedAt = rejectedAt,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        });
        await _dbContext.SaveChangesAsync();
    }

    private async Task<MemberApplication> SeedApplicationAsync(Guid applicationId,
        ApplicationStatus status = ApplicationStatus.Submitted)
    {
        var userId = Guid.NewGuid();
        _dbContext.Users.Add(new User
        {
            Id = userId,
            DisplayName = "Applicant",
            UserName = $"applicant-{userId}@test.com",
            Email = $"applicant-{userId}@test.com"
        });
        var application = new MemberApplication
        {
            Id = applicationId,
            UserId = userId,
            MembershipTier = MembershipTier.Colaborador,
            Motivation = "I want to contribute",
            SubmittedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        };
        if (status == ApplicationStatus.Approved)
            application.Approve(Guid.NewGuid(), null, _clock);
        else if (status == ApplicationStatus.Rejected)
            application.Reject(Guid.NewGuid(), "reason", _clock);
        else if (status == ApplicationStatus.Withdrawn)
            application.Withdraw(_clock);
        _dbContext.Applications.Add(application);
        await _dbContext.SaveChangesAsync();
        return application;
    }
}
