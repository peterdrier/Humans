using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Humans.Application.DTOs;
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
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

    // --- GetReviewQueueAsync ---

    [Fact]
    public async Task GetReviewQueueAsync_SeparatesFlaggedFromPending()
    {
        var noConsentId = Guid.NewGuid();
        var consentPendingId = Guid.NewGuid();
        var flaggedId = Guid.NewGuid();
        await SeedUserWithProfileAsync(noConsentId);
        await SeedUserWithProfileAsync(consentPendingId);
        await SeedUserWithProfileAsync(flaggedId);
        var consentPendingProfile = await _dbContext.Profiles.FirstAsync(p => p.UserId == consentPendingId);
        consentPendingProfile.ConsentCheckStatus = ConsentCheckStatus.Pending;
        var flaggedProfile = await _dbContext.Profiles.FirstAsync(p => p.UserId == flaggedId);
        flaggedProfile.ConsentCheckStatus = ConsentCheckStatus.Flagged;
        await _dbContext.SaveChangesAsync();

        var (pending, flagged, _) = await _service.GetReviewQueueAsync();

        pending.Should().HaveCount(2);
        pending.Select(p => p.UserId).Should().Contain(noConsentId);
        pending.Select(p => p.UserId).Should().Contain(consentPendingId);
        flagged.Should().HaveCount(1);
        flagged[0].UserId.Should().Be(flaggedId);
    }

    [Fact]
    public async Task GetReviewQueueAsync_ExcludesApproved()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId, isApproved: true);

        var (pending, flagged, _) = await _service.GetReviewQueueAsync();

        pending.Should().BeEmpty();
        flagged.Should().BeEmpty();
    }

    [Fact]
    public async Task GetReviewQueueAsync_ExcludesRejected()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId, rejectedAt: _clock.GetCurrentInstant());

        var (pending, flagged, _) = await _service.GetReviewQueueAsync();

        pending.Should().BeEmpty();
        flagged.Should().BeEmpty();
    }

    [Fact]
    public async Task GetReviewQueueAsync_IncludesPendingAppUserIds()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId);
        await SeedApplicationForUserAsync(userId, Guid.NewGuid());

        var (_, _, pendingAppUserIds) = await _service.GetReviewQueueAsync();

        pendingAppUserIds.Should().Contain(userId);
    }

    [Fact]
    public async Task GetReviewQueueAsync_OrdersByCreatedAt()
    {
        var olderId = Guid.NewGuid();
        var newerId = Guid.NewGuid();
        var now = _clock.GetCurrentInstant();

        // Seed with explicit CreatedAt (init-only property, must be set at construction)
        _dbContext.Users.Add(new User { Id = olderId, DisplayName = "Older", UserName = $"older-{olderId}@test.com", Email = $"older-{olderId}@test.com" });
        _dbContext.Profiles.Add(new Profile
        {
            Id = Guid.NewGuid(),
            UserId = olderId,
            BurnerName = "Older",
            FirstName = "Older",
            LastName = "User",
            CreatedAt = now - Duration.FromDays(10),
            UpdatedAt = now
        });
        _dbContext.Users.Add(new User { Id = newerId, DisplayName = "Newer", UserName = $"newer-{newerId}@test.com", Email = $"newer-{newerId}@test.com" });
        _dbContext.Profiles.Add(new Profile
        {
            Id = Guid.NewGuid(),
            UserId = newerId,
            BurnerName = "Newer",
            FirstName = "Newer",
            LastName = "User",
            CreatedAt = now - Duration.FromDays(1),
            UpdatedAt = now
        });
        await _dbContext.SaveChangesAsync();

        var (pending, _, _) = await _service.GetReviewQueueAsync();

        pending.Should().HaveCount(2);
        pending[0].UserId.Should().Be(olderId);
        pending[1].UserId.Should().Be(newerId);
    }

    [Fact]
    public async Task GetReviewQueueAsync_EmptyWhenNone()
    {
        var (pending, flagged, pendingAppUserIds) = await _service.GetReviewQueueAsync();

        pending.Should().BeEmpty();
        flagged.Should().BeEmpty();
        pendingAppUserIds.Should().BeEmpty();
    }

    // --- GetReviewDetailAsync ---

    [Fact]
    public async Task GetReviewDetailAsync_ReturnsProfileAndConsentCounts()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId);
        _membershipCalculator.GetMembershipSnapshotAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new MembershipSnapshot(MembershipStatus.Pending, false, 3, 1, new List<Guid>()));

        var (profile, consentCount, requiredConsentCount, _) = await _service.GetReviewDetailAsync(userId);

        profile.Should().NotBeNull();
        profile!.UserId.Should().Be(userId);
        consentCount.Should().Be(2); // 3 - 1
        requiredConsentCount.Should().Be(3);
    }

    [Fact]
    public async Task GetReviewDetailAsync_ReturnsPendingApplication()
    {
        var userId = Guid.NewGuid();
        var appId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId);
        await SeedApplicationForUserAsync(userId, appId);
        _membershipCalculator.GetMembershipSnapshotAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new MembershipSnapshot(MembershipStatus.Pending, false, 0, 0, new List<Guid>()));

        var (_, _, _, pendingApp) = await _service.GetReviewDetailAsync(userId);

        pendingApp.Should().NotBeNull();
        pendingApp!.Id.Should().Be(appId);
    }

    [Fact]
    public async Task GetReviewDetailAsync_ProfileNotFound_ReturnsNulls()
    {
        var (profile, consentCount, requiredConsentCount, pendingApp) =
            await _service.GetReviewDetailAsync(Guid.NewGuid());

        profile.Should().BeNull();
        consentCount.Should().Be(0);
        requiredConsentCount.Should().Be(0);
        pendingApp.Should().BeNull();
    }

    [Fact]
    public async Task GetReviewDetailAsync_NoPendingApp_ReturnsNullApp()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId);
        _membershipCalculator.GetMembershipSnapshotAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new MembershipSnapshot(MembershipStatus.Pending, false, 0, 0, new List<Guid>()));

        var (profile, _, _, pendingApp) = await _service.GetReviewDetailAsync(userId);

        profile.Should().NotBeNull();
        pendingApp.Should().BeNull();
    }

    // --- GetBoardVotingDashboardAsync ---

    [Fact]
    public async Task GetBoardVotingDashboardAsync_ReturnsSubmittedApplications()
    {
        await SeedApplicationAsync(Guid.NewGuid());
        await SeedApplicationAsync(Guid.NewGuid(), status: ApplicationStatus.Approved);

        var (applications, _) = await _service.GetBoardVotingDashboardAsync();

        applications.Should().HaveCount(1);
        applications[0].Status.Should().Be(ApplicationStatus.Submitted);
    }

    [Fact]
    public async Task GetBoardVotingDashboardAsync_OrdersByTierThenSubmittedAt()
    {
        // Colaborador = 0, Asociado = 1 — Colaborador should come first
        var colabId = Guid.NewGuid();
        var asocId = Guid.NewGuid();
        await SeedApplicationAsync(colabId, tier: MembershipTier.Colaborador);
        await SeedApplicationAsync(asocId, tier: MembershipTier.Asociado);

        var (applications, _) = await _service.GetBoardVotingDashboardAsync();

        applications.Should().HaveCount(2);
        applications[0].Id.Should().Be(colabId);
        applications[1].Id.Should().Be(asocId);
    }

    [Fact]
    public async Task GetBoardVotingDashboardAsync_ReturnsCurrentBoardMembers()
    {
        var boardMemberId = Guid.NewGuid();
        var now = _clock.GetCurrentInstant();
        _dbContext.Users.Add(new User { Id = boardMemberId, DisplayName = "Board Member", UserName = "board@test.com", Email = "board@test.com" });
        _dbContext.RoleAssignments.Add(new RoleAssignment
        {
            Id = Guid.NewGuid(),
            UserId = boardMemberId,
            RoleName = RoleNames.Board,
            ValidFrom = now - Duration.FromDays(30),
            ValidTo = null,
            CreatedAt = now,
            CreatedByUserId = Guid.NewGuid()
        });
        await _dbContext.SaveChangesAsync();

        var (_, boardMembers) = await _service.GetBoardVotingDashboardAsync();

        boardMembers.Should().HaveCount(1);
        boardMembers[0].UserId.Should().Be(boardMemberId);
        boardMembers[0].DisplayName.Should().Be("Board Member");
    }

    [Fact]
    public async Task GetBoardVotingDashboardAsync_ExcludesExpiredBoardMembers()
    {
        var boardMemberId = Guid.NewGuid();
        var now = _clock.GetCurrentInstant();
        _dbContext.Users.Add(new User { Id = boardMemberId, DisplayName = "Expired Board", UserName = "expired@test.com", Email = "expired@test.com" });
        _dbContext.RoleAssignments.Add(new RoleAssignment
        {
            Id = Guid.NewGuid(),
            UserId = boardMemberId,
            RoleName = RoleNames.Board,
            ValidFrom = now - Duration.FromDays(365),
            ValidTo = now - Duration.FromDays(30),
            CreatedAt = now - Duration.FromDays(365),
            CreatedByUserId = Guid.NewGuid()
        });
        await _dbContext.SaveChangesAsync();

        var (_, boardMembers) = await _service.GetBoardVotingDashboardAsync();

        boardMembers.Should().BeEmpty();
    }

    [Fact]
    public async Task GetBoardVotingDashboardAsync_BoardMembersOrderedByDisplayName()
    {
        var now = _clock.GetCurrentInstant();
        var zoeId = Guid.NewGuid();
        var alphaId = Guid.NewGuid();
        _dbContext.Users.Add(new User { Id = zoeId, DisplayName = "Zoe", UserName = "zoe@test.com", Email = "zoe@test.com" });
        _dbContext.Users.Add(new User { Id = alphaId, DisplayName = "Alpha", UserName = "alpha@test.com", Email = "alpha@test.com" });
        _dbContext.RoleAssignments.Add(new RoleAssignment
        {
            Id = Guid.NewGuid(),
            UserId = zoeId,
            RoleName = RoleNames.Board,
            ValidFrom = now - Duration.FromDays(30),
            ValidTo = null,
            CreatedAt = now,
            CreatedByUserId = Guid.NewGuid()
        });
        _dbContext.RoleAssignments.Add(new RoleAssignment
        {
            Id = Guid.NewGuid(),
            UserId = alphaId,
            RoleName = RoleNames.Board,
            ValidFrom = now - Duration.FromDays(30),
            ValidTo = null,
            CreatedAt = now,
            CreatedByUserId = Guid.NewGuid()
        });
        await _dbContext.SaveChangesAsync();

        var (_, boardMembers) = await _service.GetBoardVotingDashboardAsync();

        boardMembers.Should().HaveCount(2);
        boardMembers[0].DisplayName.Should().Be("Alpha");
        boardMembers[1].DisplayName.Should().Be("Zoe");
    }

    // --- GetBoardVotingDetailAsync ---

    [Fact]
    public async Task GetBoardVotingDetailAsync_ReturnsApplicationWithIncludes()
    {
        var appId = Guid.NewGuid();
        await SeedApplicationAsync(appId);
        _dbContext.BoardVotes.Add(new BoardVote
        {
            Id = Guid.NewGuid(),
            ApplicationId = appId,
            BoardMemberUserId = Guid.NewGuid(),
            Vote = VoteChoice.Yay,
            VotedAt = _clock.GetCurrentInstant()
        });
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetBoardVotingDetailAsync(appId);

        result.Should().NotBeNull();
        result!.Id.Should().Be(appId);
        result.BoardVotes.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetBoardVotingDetailAsync_NonExistent_ReturnsNull()
    {
        var result = await _service.GetBoardVotingDetailAsync(Guid.NewGuid());

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetBoardVotingDetailAsync_IncludesVoterInfo()
    {
        var appId = Guid.NewGuid();
        var voterId = Guid.NewGuid();
        await SeedApplicationAsync(appId);
        _dbContext.Users.Add(new User { Id = voterId, DisplayName = "Voter", UserName = "voter@test.com", Email = "voter@test.com" });
        _dbContext.BoardVotes.Add(new BoardVote
        {
            Id = Guid.NewGuid(),
            ApplicationId = appId,
            BoardMemberUserId = voterId,
            Vote = VoteChoice.No,
            VotedAt = _clock.GetCurrentInstant()
        });
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetBoardVotingDetailAsync(appId);

        result.Should().NotBeNull();
        var vote = result!.BoardVotes.First();
        vote.BoardMemberUser.Should().NotBeNull();
        vote.BoardMemberUser.DisplayName.Should().Be("Voter");
    }

    // --- HasBoardVotesAsync ---

    [Fact]
    public async Task HasBoardVotesAsync_HasVotes_ReturnsTrue()
    {
        var appId = Guid.NewGuid();
        await SeedApplicationAsync(appId);
        _dbContext.BoardVotes.Add(new BoardVote
        {
            Id = Guid.NewGuid(),
            ApplicationId = appId,
            BoardMemberUserId = Guid.NewGuid(),
            Vote = VoteChoice.Yay,
            VotedAt = _clock.GetCurrentInstant()
        });
        await _dbContext.SaveChangesAsync();

        var result = await _service.HasBoardVotesAsync(appId);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task HasBoardVotesAsync_NoVotes_ReturnsFalse()
    {
        var appId = Guid.NewGuid();
        await SeedApplicationAsync(appId);

        var result = await _service.HasBoardVotesAsync(appId);

        result.Should().BeFalse();
    }

    // --- GetAdminDashboardAsync ---

    [Fact]
    public async Task GetAdminDashboardAsync_TotalMembers_CountsAllUsers()
    {
        await SeedUserWithProfileAsync(Guid.NewGuid());
        await SeedUserWithProfileAsync(Guid.NewGuid());
        SetupMembershipCalculatorDefaults();

        var result = await _service.GetAdminDashboardAsync();

        result.TotalMembers.Should().Be(2);
    }

    [Fact]
    public async Task GetAdminDashboardAsync_ActiveMembers_CountsNonSuspended()
    {
        await SeedUserWithProfileAsync(Guid.NewGuid());
        await SeedUserWithProfileAsync(Guid.NewGuid(), isSuspended: true);
        SetupMembershipCalculatorDefaults();

        var result = await _service.GetAdminDashboardAsync();

        result.ActiveMembers.Should().Be(1);
    }

    [Fact]
    public async Task GetAdminDashboardAsync_PendingVolunteers_CountsUnapprovedNonSuspended()
    {
        await SeedUserWithProfileAsync(Guid.NewGuid(), isApproved: false);
        await SeedUserWithProfileAsync(Guid.NewGuid(), isApproved: true);
        await SeedUserWithProfileAsync(Guid.NewGuid(), isApproved: false, isSuspended: true);
        SetupMembershipCalculatorDefaults();

        var result = await _service.GetAdminDashboardAsync();

        result.PendingVolunteers.Should().Be(1);
    }

    [Fact]
    public async Task GetAdminDashboardAsync_PendingApplications_CountsSubmitted()
    {
        await SeedApplicationAsync(Guid.NewGuid());
        await SeedApplicationAsync(Guid.NewGuid(), status: ApplicationStatus.Approved);
        SetupMembershipCalculatorDefaults();

        var result = await _service.GetAdminDashboardAsync();

        result.PendingApplications.Should().Be(1);
    }

    [Fact]
    public async Task GetAdminDashboardAsync_PendingConsents_CalculatedFromMembershipCalculator()
    {
        var userId1 = Guid.NewGuid();
        var userId2 = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId1);
        await SeedUserWithProfileAsync(userId2);
        // Nobody has all consents → all users have pending consents
        _membershipCalculator.GetUsersWithAllRequiredConsentsAsync(Arg.Any<IEnumerable<Guid>>())
            .Returns(new HashSet<Guid>());
        _membershipCalculator.GetUsersWithAllRequiredConsentsForTeamAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<Guid>())
            .Returns(new HashSet<Guid>());

        var result = await _service.GetAdminDashboardAsync();

        result.PendingConsents.Should().Be(2);
    }

    [Fact]
    public async Task GetAdminDashboardAsync_AppStats_ExcludesWithdrawn()
    {
        await SeedApplicationAsync(Guid.NewGuid());
        await SeedApplicationAsync(Guid.NewGuid(), status: ApplicationStatus.Approved);
        await SeedApplicationAsync(Guid.NewGuid(), status: ApplicationStatus.Withdrawn);
        SetupMembershipCalculatorDefaults();

        var result = await _service.GetAdminDashboardAsync();

        result.TotalApplications.Should().Be(2);
    }

    [Fact]
    public async Task GetAdminDashboardAsync_AppStats_IncludesTierBreakdown()
    {
        await SeedApplicationAsync(Guid.NewGuid(), tier: MembershipTier.Colaborador);
        await SeedApplicationAsync(Guid.NewGuid(), tier: MembershipTier.Asociado);
        SetupMembershipCalculatorDefaults();

        var result = await _service.GetAdminDashboardAsync();

        result.ColaboradorApplied.Should().Be(1);
        result.AsociadoApplied.Should().Be(1);
    }

    [Fact]
    public async Task GetAdminDashboardAsync_ReturnsZeros_WhenEmpty()
    {
        SetupMembershipCalculatorDefaults();

        var result = await _service.GetAdminDashboardAsync();

        result.TotalMembers.Should().Be(0);
        result.ActiveMembers.Should().Be(0);
        result.PendingVolunteers.Should().Be(0);
        result.PendingApplications.Should().Be(0);
        result.PendingConsents.Should().Be(0);
        result.TotalApplications.Should().Be(0);
    }

    [Fact]
    public async Task GetAdminDashboardAsync_LeadsMissingConsents_CountTowardPending()
    {
        var leadUserId = Guid.NewGuid();
        await SeedUserWithProfileAsync(leadUserId);
        var now = _clock.GetCurrentInstant();
        var teamId = Guid.NewGuid();
        _dbContext.Teams.Add(new Team { Id = teamId, Name = "Test", Slug = "test", SystemTeamType = SystemTeamType.None, IsActive = true, CreatedAt = now, UpdatedAt = now });
        _dbContext.TeamMembers.Add(new TeamMember { Id = Guid.NewGuid(), TeamId = teamId, UserId = leadUserId, Role = TeamMemberRole.Lead, JoinedAt = now });
        await _dbContext.SaveChangesAsync();

        // User has all volunteer consents but NOT lead consents
        _membershipCalculator.GetUsersWithAllRequiredConsentsAsync(Arg.Any<IEnumerable<Guid>>())
            .Returns(new HashSet<Guid> { leadUserId });
        _membershipCalculator.GetUsersWithAllRequiredConsentsForTeamAsync(Arg.Any<IEnumerable<Guid>>(), SystemTeamIds.Leads)
            .Returns(new HashSet<Guid>()); // lead missing lead-specific consents

        var result = await _service.GetAdminDashboardAsync();

        result.PendingConsents.Should().Be(1);
    }

    // --- Helpers ---

    private async Task SeedProfileAsync(Guid userId,
        bool isApproved = false, bool isSuspended = false,
        ConsentCheckStatus? consentCheckStatus = null,
        Instant? rejectedAt = null)
    {
        if (!await _dbContext.Users.AnyAsync(u => u.Id == userId))
        {
            _dbContext.Users.Add(new User
            {
                Id = userId,
                DisplayName = "Test User",
                UserName = $"test-{userId}@test.com",
                Email = $"test-{userId}@test.com",
                PreferredLanguage = "en"
            });
        }
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
        ApplicationStatus status = ApplicationStatus.Submitted,
        MembershipTier tier = MembershipTier.Colaborador)
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
            MembershipTier = tier,
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

    private async Task SeedApplicationForUserAsync(Guid userId, Guid applicationId,
        ApplicationStatus status = ApplicationStatus.Submitted,
        MembershipTier tier = MembershipTier.Colaborador)
    {
        var application = new MemberApplication
        {
            Id = applicationId,
            UserId = userId,
            MembershipTier = tier,
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
    }

    private void SetupMembershipCalculatorDefaults()
    {
        _membershipCalculator.GetUsersWithAllRequiredConsentsAsync(Arg.Any<IEnumerable<Guid>>())
            .Returns(ci => new HashSet<Guid>(ci.Arg<IEnumerable<Guid>>()));
        _membershipCalculator.GetUsersWithAllRequiredConsentsForTeamAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<Guid>())
            .Returns(ci => new HashSet<Guid>(ci.Arg<IEnumerable<Guid>>()));
    }
}
