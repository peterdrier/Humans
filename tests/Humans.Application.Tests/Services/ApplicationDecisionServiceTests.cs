using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Services.Governance;
using Humans.Domain;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using MemberApplication = Humans.Domain.Entities.Application;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Users;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Tests.Infrastructure;
using Humans.Infrastructure.Repositories.Governance;

namespace Humans.Application.Tests.Services;

public sealed class ApplicationDecisionServiceTests : ServiceTestHarness
{
    private readonly ApplicationRepository _repository;
    private readonly IUserService _userService;
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();
    private readonly INotificationService _notificationService = Substitute.For<INotificationService>();
    private readonly ISystemTeamSync _syncJob = Substitute.For<ISystemTeamSync>();
    private readonly IHumansMetrics _metrics = Substitute.For<IHumansMetrics>();
    private readonly INavBadgeCacheInvalidator _navBadge = Substitute.For<INavBadgeCacheInvalidator>();
    private readonly INotificationMeterCacheInvalidator _notificationMeter = Substitute.For<INotificationMeterCacheInvalidator>();
    private readonly IVotingBadgeCacheInvalidator _votingBadge = Substitute.For<IVotingBadgeCacheInvalidator>();
    private readonly IRoleAssignmentService _roleAssignmentService = Substitute.For<IRoleAssignmentService>();
    private readonly IUserEmailService _userEmailService = Substitute.For<IUserEmailService>();
    private readonly ApplicationDecisionService _service;

    public ApplicationDecisionServiceTests()
    {
        _repository = new ApplicationRepository(DbFactory);
        _userService = NewDbBackedUserService();

        _userEmailService.GetNotificationTargetEmailsAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>()));

        _service = new ApplicationDecisionService(
            _repository,
            _userService,
            _roleAssignmentService,
            AuditLog,
            _emailService,
            _userEmailService,
            _notificationService,
            _syncJob,
            _metrics,
            _navBadge,
            _notificationMeter,
            _votingBadge,
            Clock,
            NullLogger<ApplicationDecisionService>.Instance);
    }

    // --- Submit flow ---

    [HumansFact]
    public async Task SubmitAsync_ValidColaborador_CreatesApplication()
    {
        var userId = Guid.NewGuid();

        var result = await _service.SubmitAsync(
            userId, MembershipTier.Colaborador, "I want to contribute",
            "Extra info", null, null, "en");

        result.Success.Should().BeTrue();
        result.ApplicationId.Should().NotBeNull();
        var app = await Db.Applications.FirstAsync();
        app.MembershipTier.Should().Be(MembershipTier.Colaborador);
        app.Motivation.Should().Be("I want to contribute");
        app.Status.Should().Be(ApplicationStatus.Submitted);
    }

    [HumansFact]
    public async Task SubmitAsync_Asociado_IncludesExtraFields()
    {
        var userId = Guid.NewGuid();

        await _service.SubmitAsync(
            userId, MembershipTier.Asociado, "Motivation",
            null, "My contribution", "I understand the role", "es");

        var app = await Db.Applications.FirstAsync();
        app.SignificantContribution.Should().Be("My contribution");
        app.RoleUnderstanding.Should().Be("I understand the role");
    }

    [HumansFact]
    public async Task SubmitAsync_Colaborador_ExcludesAsociadoFields()
    {
        var userId = Guid.NewGuid();

        await _service.SubmitAsync(
            userId, MembershipTier.Colaborador, "Motivation",
            null, "Should be ignored", "Also ignored", "en");

        var app = await Db.Applications.FirstAsync();
        app.SignificantContribution.Should().BeNull();
        app.RoleUnderstanding.Should().BeNull();
    }

    [HumansFact]
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

    [HumansFact]
    public async Task SubmitAsync_VolunteerTier_ReturnsInvalidTier()
    {
        var result = await _service.SubmitAsync(
            Guid.NewGuid(), MembershipTier.Volunteer, "Motivation",
            null, null, null, "en");

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("InvalidTier");
        (await Db.Applications.CountAsync()).Should().Be(0);
    }

    [HumansFact]
    public async Task SubmitAsync_AsociadoMissingRequiredFields_ReturnsFieldError()
    {
        var result = await _service.SubmitAsync(
            Guid.NewGuid(), MembershipTier.Asociado, "Motivation",
            null, "   ", null, "en");

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("SignificantContributionRequired");
        (await Db.Applications.CountAsync()).Should().Be(0);
    }

    [HumansFact]
    public async Task SubmitAsync_InvalidatesNavBadgeAndNotificationMeter()
    {
        var userId = Guid.NewGuid();

        var result = await _service.SubmitAsync(
            userId, MembershipTier.Colaborador, "Motivation",
            null, null, null, "en");

        result.Success.Should().BeTrue();
        _navBadge.Received().Invalidate();
        _notificationMeter.Received().Invalidate();
    }

    // --- Withdraw flow ---

    [HumansFact]
    public async Task WithdrawAsync_SubmittedApplication_SetsWithdrawn()
    {
        var userId = Guid.NewGuid();
        var app = await SeedSubmittedApplicationAsync(userId);

        var result = await _service.WithdrawAsync(app.Id, userId);

        result.Success.Should().BeTrue();
        Db.ChangeTracker.Clear();
        var updated = await Db.Applications.FirstAsync(a => a.Id == app.Id);
        updated.Status.Should().Be(ApplicationStatus.Withdrawn);
        _metrics.Received().RecordApplicationProcessed("withdrawn");
    }

    [HumansFact]
    public async Task WithdrawAsync_NotSubmitted_ReturnsError()
    {
        var userId = Guid.NewGuid();
        var app = await SeedSubmittedApplicationAsync(userId);
        app.Withdraw(Clock);
        await Db.SaveChangesAsync();

        var result = await _service.WithdrawAsync(app.Id, userId);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("CannotWithdraw");
    }

    [HumansFact]
    public async Task WithdrawAsync_WrongUser_ReturnsNotFound()
    {
        var userId = Guid.NewGuid();
        var app = await SeedSubmittedApplicationAsync(userId);

        var result = await _service.WithdrawAsync(app.Id, Guid.NewGuid());

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("NotFound");
    }

    // --- Approve flow ---

    [HumansFact]
    public async Task ApproveAsync_SubmittedApplication_SetsApproved()
    {
        var app = await SeedSubmittedApplicationAsync(Guid.NewGuid());

        var result = await _service.ApproveAsync(app.Id, Guid.NewGuid(), "Approved", null);

        result.Success.Should().BeTrue();
        Db.ChangeTracker.Clear();
        var updated = await Db.Applications.FirstAsync(a => a.Id == app.Id);
        updated.Status.Should().Be(ApplicationStatus.Approved);
    }

    [HumansFact]
    public async Task ApproveAsync_SetsTermExpiry()
    {
        var app = await SeedSubmittedApplicationAsync(Guid.NewGuid());

        await _service.ApproveAsync(app.Id, Guid.NewGuid(), null, null);

        Db.ChangeTracker.Clear();
        var updated = await Db.Applications.FirstAsync(a => a.Id == app.Id);
        var today = Clock.GetCurrentInstant().InUtc().Date;
        var expectedExpiry = TermExpiryCalculator.ComputeTermExpiry(today);
        updated.TermExpiresAt.Should().Be(expectedExpiry);
    }

    [HumansFact]
    public async Task ApproveAsync_UpdatesProfileTierViaUserService()
    {
        var userId = Guid.NewGuid();
        var app = await SeedSubmittedApplicationAsync(userId, MembershipTier.Asociado);

        await _service.ApproveAsync(app.Id, Guid.NewGuid(), null, null);

        await _userService.Received().SetMembershipTierAsync(
            userId, MembershipTier.Asociado, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ApproveAsync_DeletesBoardVotes()
    {
        var app = await SeedSubmittedApplicationAsync(Guid.NewGuid());
        Db.BoardVotes.Add(new BoardVote
        {
            Id = Guid.NewGuid(),
            ApplicationId = app.Id,
            BoardMemberUserId = Guid.NewGuid(),
            Vote = VoteChoice.Yay,
            VotedAt = Clock.GetCurrentInstant()
        });
        await Db.SaveChangesAsync();

        await _service.ApproveAsync(app.Id, Guid.NewGuid(), null, null);

        var votes = await Db.BoardVotes.Where(v => v.ApplicationId == app.Id).ToListAsync();
        votes.Should().BeEmpty();
    }

    [HumansFact]
    public async Task ApproveAsync_SyncsColaboradorTeam()
    {
        var userId = Guid.NewGuid();
        var app = await SeedSubmittedApplicationAsync(userId, MembershipTier.Colaborador);

        await _service.ApproveAsync(app.Id, Guid.NewGuid(), null, null);

        await _syncJob.Received().SyncMembershipForUserAsync(
            userId, SystemTeamType.Colaboradors, Arg.Any<CancellationToken>());
        await _syncJob.DidNotReceive().SyncMembershipForUserAsync(
            Arg.Any<Guid>(), SystemTeamType.Asociados, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ApproveAsync_SyncsAsociadoTeam()
    {
        var userId = Guid.NewGuid();
        var app = await SeedSubmittedApplicationAsync(userId, MembershipTier.Asociado);

        await _service.ApproveAsync(app.Id, Guid.NewGuid(), null, null);

        await _syncJob.Received().SyncMembershipForUserAsync(
            userId, SystemTeamType.Asociados, Arg.Any<CancellationToken>());
        await _syncJob.DidNotReceive().SyncMembershipForUserAsync(
            Arg.Any<Guid>(), SystemTeamType.Colaboradors, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ApproveAsync_InvalidatesNavBadgeAndNotificationMeter()
    {
        var app = await SeedSubmittedApplicationAsync(Guid.NewGuid());

        await _service.ApproveAsync(app.Id, Guid.NewGuid(), null, null);

        _navBadge.Received().Invalidate();
        _notificationMeter.Received().Invalidate();
    }

    [HumansFact]
    public async Task ApproveAsync_InvalidatesEveryVoterBadge()
    {
        var app = await SeedSubmittedApplicationAsync(Guid.NewGuid());
        var voter1 = Guid.NewGuid();
        var voter2 = Guid.NewGuid();
        await Db.BoardVotes.AddRangeAsync(
            new BoardVote
            {
                Id = Guid.NewGuid(),
                ApplicationId = app.Id,
                BoardMemberUserId = voter1,
                Vote = VoteChoice.Yay,
                VotedAt = Clock.GetCurrentInstant()
            },
            new BoardVote
            {
                Id = Guid.NewGuid(),
                ApplicationId = app.Id,
                BoardMemberUserId = voter2,
                Vote = VoteChoice.No,
                VotedAt = Clock.GetCurrentInstant()
            });
        await Db.SaveChangesAsync();

        await _service.ApproveAsync(app.Id, Guid.NewGuid(), null, null);

        _votingBadge.Received().Invalidate(voter1);
        _votingBadge.Received().Invalidate(voter2);
    }

    [HumansFact]
    public async Task ApproveAsync_NotSubmitted_ReturnsError()
    {
        var app = await SeedSubmittedApplicationAsync(Guid.NewGuid());
        app.Withdraw(Clock);
        await Db.SaveChangesAsync();

        var result = await _service.ApproveAsync(app.Id, Guid.NewGuid(), null, null);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("NotSubmitted");
    }

    [HumansFact]
    public async Task ApproveAsync_NotFound_ReturnsError()
    {
        var result = await _service.ApproveAsync(Guid.NewGuid(), Guid.NewGuid(), null, null);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("NotFound");
    }

    [HumansFact]
    public async Task ApproveAsync_EmailsApplicantViaUserServiceLookup()
    {
        var userId = Guid.NewGuid();
        var app = await SeedSubmittedApplicationAsync(userId, MembershipTier.Colaborador);
        var user = new User
        {
            Id = userId,
            DisplayName = "Alice",
            UserName = "alice@test.com",
            Email = "alice@test.com",
            PreferredLanguage = "en"
        };
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(user.ToUserInfo());
        _userEmailService.GetNotificationTargetEmailsAsync(
                Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(userId)),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<Guid, string>>(
                new Dictionary<Guid, string> { [userId] = "alice@test.com" }));

        await _service.ApproveAsync(app.Id, Guid.NewGuid(), null, null);

        await _emailService.Received().SendApplicationApprovedAsync(
            "alice@test.com",
            "Alice",
            MembershipTier.Colaborador,
            "en");
    }

    [HumansFact]
    public async Task ApproveAsync_ResolvesRecipientFromUserEmailsWhenUserEmailColumnIsNull()
    {
        var userId = Guid.NewGuid();
        var app = await SeedSubmittedApplicationAsync(userId, MembershipTier.Colaborador);
        var user = new User
        {
            Id = userId,
            DisplayName = "Bob",
            UserName = "bob",
            Email = null,
            PreferredLanguage = "en"
        };
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(user.ToUserInfo());
        _userEmailService.GetNotificationTargetEmailsAsync(
                Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(userId)),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<Guid, string>>(
                new Dictionary<Guid, string> { [userId] = "bob.notify@test.com" }));

        await _service.ApproveAsync(app.Id, Guid.NewGuid(), null, null);

        await _emailService.Received().SendApplicationApprovedAsync(
            "bob.notify@test.com",
            "Bob",
            MembershipTier.Colaborador,
            "en");
        await _emailService.DidNotReceive().SendApplicationApprovedAsync(
            string.Empty,
            Arg.Any<string>(),
            Arg.Any<MembershipTier>(),
            Arg.Any<string>());
    }

    [HumansFact]
    public async Task ApproveAsync_SkipsEmailWhenNoNotificationTargetResolved()
    {
        var userId = Guid.NewGuid();
        var app = await SeedSubmittedApplicationAsync(userId, MembershipTier.Colaborador);
        var user = new User
        {
            Id = userId,
            DisplayName = "Carol",
            UserName = "carol",
            Email = null,
            PreferredLanguage = "en"
        };
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(UserInfo.Create(user, [], [], [], null, [], [], [], []));

        await _service.ApproveAsync(app.Id, Guid.NewGuid(), null, null);

        await _emailService.DidNotReceive().SendApplicationApprovedAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<MembershipTier>(),
            Arg.Any<string>());
    }

    // --- Reject flow ---

    [HumansFact]
    public async Task RejectAsync_SubmittedApplication_SetsRejected()
    {
        var app = await SeedSubmittedApplicationAsync(Guid.NewGuid());

        var result = await _service.RejectAsync(app.Id, Guid.NewGuid(), "Not ready", null);

        result.Success.Should().BeTrue();
        Db.ChangeTracker.Clear();
        var updated = await Db.Applications.FirstAsync(a => a.Id == app.Id);
        updated.Status.Should().Be(ApplicationStatus.Rejected);
        updated.DecisionNote.Should().Be("Not ready");
    }

    [HumansFact]
    public async Task RejectAsync_DeletesBoardVotes()
    {
        var app = await SeedSubmittedApplicationAsync(Guid.NewGuid());
        Db.BoardVotes.Add(new BoardVote
        {
            Id = Guid.NewGuid(),
            ApplicationId = app.Id,
            BoardMemberUserId = Guid.NewGuid(),
            Vote = VoteChoice.No,
            VotedAt = Clock.GetCurrentInstant()
        });
        await Db.SaveChangesAsync();

        await _service.RejectAsync(app.Id, Guid.NewGuid(), "reason", null);

        var votes = await Db.BoardVotes.Where(v => v.ApplicationId == app.Id).ToListAsync();
        votes.Should().BeEmpty();
    }

    [HumansFact]
    public async Task RejectAsync_DoesNotUpdateProfileTier()
    {
        var userId = Guid.NewGuid();
        var app = await SeedSubmittedApplicationAsync(userId, MembershipTier.Asociado);

        await _service.RejectAsync(app.Id, Guid.NewGuid(), "reason", null);

        await _userService.DidNotReceive().SetMembershipTierAsync(
            Arg.Any<Guid>(), Arg.Any<MembershipTier>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task RejectAsync_DoesNotSyncTeams()
    {
        var app = await SeedSubmittedApplicationAsync(Guid.NewGuid());

        await _service.RejectAsync(app.Id, Guid.NewGuid(), "reason", null);

        await _syncJob.DidNotReceive().SyncMembershipForUserAsync(
            Arg.Any<Guid>(), SystemTeamType.Colaboradors, Arg.Any<CancellationToken>());
        await _syncJob.DidNotReceive().SyncMembershipForUserAsync(
            Arg.Any<Guid>(), SystemTeamType.Asociados, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task RejectAsync_NotSubmitted_ReturnsError()
    {
        var app = await SeedSubmittedApplicationAsync(Guid.NewGuid());
        app.Withdraw(Clock);
        await Db.SaveChangesAsync();

        var result = await _service.RejectAsync(app.Id, Guid.NewGuid(), "reason", null);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("NotSubmitted");
    }

    // --- GetUserApplicationsAsync ---

    [HumansFact]
    public async Task GetUserApplicationsAsync_ReturnsAllStatusesForUser()
    {
        var userId = Guid.NewGuid();
        var app1 = new MemberApplication
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            MembershipTier = MembershipTier.Colaborador,
            Motivation = "M1",
            SubmittedAt = Clock.GetCurrentInstant() - Duration.FromDays(2),
            UpdatedAt = Clock.GetCurrentInstant()
        };
        var app2 = new MemberApplication
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            MembershipTier = MembershipTier.Asociado,
            Motivation = "M2",
            SubmittedAt = Clock.GetCurrentInstant() - Duration.FromDays(1),
            UpdatedAt = Clock.GetCurrentInstant()
        };
        var app3 = new MemberApplication
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            MembershipTier = MembershipTier.Colaborador,
            Motivation = "M3",
            SubmittedAt = Clock.GetCurrentInstant(),
            UpdatedAt = Clock.GetCurrentInstant()
        };
        await Db.Applications.AddRangeAsync(app1, app2, app3);
        app1.Approve(Guid.NewGuid(), "ok", Clock);
        app2.Withdraw(Clock);
        await Db.SaveChangesAsync();

        var result = await _service.GetUserApplicationsAsync(userId);

        // Display ordering (SubmittedAt desc) now lives in the controller per the
        // DisplaySortInControllers rule; the service returns all of the user's
        // applications regardless of order.
        result.Should().HaveCount(3);
        result.Select(a => a.Id).Should().BeEquivalentTo([app1.Id, app2.Id, app3.Id]);
    }

    [HumansFact]
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

    [HumansFact]
    public async Task GetUserApplicationsAsync_EmptyForNoApps()
    {
        var result = await _service.GetUserApplicationsAsync(Guid.NewGuid());

        result.Should().BeEmpty();
    }

    // --- GetUserApplicationDetailAsync ---

    [HumansFact]
    public async Task GetUserApplicationDetailAsync_ReturnsStitchedUserDetailDto()
    {
        var userId = Guid.NewGuid();
        var reviewerId = Guid.NewGuid();
        var reviewer = new User
        {
            Id = reviewerId,
            DisplayName = "Reviewer",
            UserName = "r@t.com",
            Email = "r@t.com"
        };
        var users = new Dictionary<Guid, User> { [reviewerId] = reviewer };
        _userService.GetByIdsAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(reviewerId)),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<Guid, User>>(users));
        _userService.GetUserInfosAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(reviewerId)),
            Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(
                new Dictionary<Guid, UserInfo> { [reviewerId] = reviewer.ToUserInfo() }));

        var app = await SeedSubmittedApplicationAsync(userId);
        app.Approve(reviewerId, "Good", Clock);
        await Db.SaveChangesAsync();

        var result = await _service.GetUserApplicationDetailAsync(app.Id, userId);

        result.Should().NotBeNull();
        result.ReviewerName.Should().Be("Reviewer");
        result.History.Should().NotBeEmpty();
        result.History[0].ChangedByDisplayName.Should().Be("Reviewer");
    }

    [HumansFact]
    public async Task GetUserApplicationDetailAsync_WrongUser_ReturnsNull()
    {
        var app = await SeedSubmittedApplicationAsync(Guid.NewGuid());

        var result = await _service.GetUserApplicationDetailAsync(app.Id, Guid.NewGuid());

        result.Should().BeNull();
    }

    [HumansFact]
    public async Task GetUserApplicationDetailAsync_NonExistent_ReturnsNull()
    {
        var result = await _service.GetUserApplicationDetailAsync(Guid.NewGuid(), Guid.NewGuid());

        result.Should().BeNull();
    }

    [HumansFact]
    public async Task GetUserApplicationDetailAsync_IncludesStateHistory()
    {
        var userId = Guid.NewGuid();
        var app = await SeedSubmittedApplicationAsync(userId);
        app.Withdraw(Clock);
        await Db.SaveChangesAsync();

        var result = await _service.GetUserApplicationDetailAsync(app.Id, userId);

        result.Should().NotBeNull();
        result.History.Should().HaveCount(1);
        result.History[0].Status.Should().Be(ApplicationStatus.Withdrawn);
    }

    // --- GetFilteredApplicationsAsync ---

    [HumansFact]
    public async Task GetFilteredApplicationsAsync_DefaultsToSubmitted()
    {
        var submittedApp = await SeedSubmittedApplicationAsync(Guid.NewGuid());
        var approvedApp = await SeedSubmittedApplicationAsync(Guid.NewGuid());
        approvedApp.Approve(Guid.NewGuid(), "ok", Clock);
        await Db.SaveChangesAsync();

        var (items, totalCount) = await _service.GetFilteredApplicationsAsync(null, null, 1, 10);

        totalCount.Should().Be(1);
        items.Should().HaveCount(1);
        items[0].Id.Should().Be(submittedApp.Id);
        items[0].Status.Should().Be(ApplicationStatus.Submitted);
    }

    [HumansFact]
    public async Task GetFilteredApplicationsAsync_FiltersByStatus()
    {
        await SeedSubmittedApplicationAsync(Guid.NewGuid());
        var approvedApp = await SeedSubmittedApplicationAsync(Guid.NewGuid());
        approvedApp.Approve(Guid.NewGuid(), "ok", Clock);
        await Db.SaveChangesAsync();

        var (items, totalCount) = await _service.GetFilteredApplicationsAsync("Approved", null, 1, 10);

        totalCount.Should().Be(1);
        items.Should().HaveCount(1);
        items[0].Id.Should().Be(approvedApp.Id);
    }

    [HumansFact]
    public async Task GetFilteredApplicationsAsync_FiltersByTier()
    {
        await SeedSubmittedApplicationAsync(Guid.NewGuid(), MembershipTier.Colaborador);
        var asociadoApp = await SeedSubmittedApplicationAsync(Guid.NewGuid(), MembershipTier.Asociado);

        var (items, totalCount) = await _service.GetFilteredApplicationsAsync(null, "Asociado", 1, 10);

        totalCount.Should().Be(1);
        items[0].MembershipTier.Should().Be(MembershipTier.Asociado);
    }

    [HumansFact]
    public async Task GetFilteredApplicationsAsync_Pagination()
    {
        for (var i = 0; i < 3; i++)
            await SeedSubmittedApplicationAsync(Guid.NewGuid());

        var (items, totalCount) = await _service.GetFilteredApplicationsAsync(null, null, 1, 2);

        totalCount.Should().Be(3);
        items.Should().HaveCount(2);
    }

    [HumansFact]
    public async Task GetFilteredApplicationsAsync_StitchesApplicantInfo()
    {
        var userId = Guid.NewGuid();
        await SeedSubmittedApplicationAsync(userId);
        var user = new User
        {
            Id = userId,
            DisplayName = "Applicant",
            UserName = "a@t.com",
            Email = "a@t.com"
        };
        _userService.GetByIdsAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(userId)),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<Guid, User>>(
                new Dictionary<Guid, User> { [userId] = user }));
        _userService.GetUserInfosAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(userId)),
            Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(
                new Dictionary<Guid, UserInfo> { [userId] = user.ToUserInfo() }));

        var (items, _) = await _service.GetFilteredApplicationsAsync(null, null, 1, 10);

        items.Should().HaveCount(1);
        items[0].UserDisplayName.Should().Be("Applicant");
        items[0].UserEmail.Should().Be("a@t.com");
    }

    // --- GetApplicationDetailAsync (admin) ---

    [HumansFact]
    public async Task GetApplicationDetailAsync_ReturnsStitchedAdminDetailDto()
    {
        var applicantId = Guid.NewGuid();
        var reviewerId = Guid.NewGuid();
        var applicant = new User
        {
            Id = applicantId,
            DisplayName = "Applicant",
            UserName = "a@t.com",
            Email = "a@t.com",
            ProfilePictureUrl = "https://example.com/pic.png"
        };
        var reviewer = new User
        {
            Id = reviewerId,
            DisplayName = "Admin",
            UserName = "r@t.com",
            Email = "r@t.com"
        };
        _userService.GetByIdsAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<Guid, User>>(
                new Dictionary<Guid, User>
                {
                    [applicantId] = applicant,
                    [reviewerId] = reviewer
                }));
        _userService.GetUserInfosAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(),
            Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(
                new Dictionary<Guid, UserInfo>
                {
                    [applicantId] = applicant.ToUserInfo(),
                    [reviewerId] = reviewer.ToUserInfo()
                }));

        var app = await SeedSubmittedApplicationAsync(applicantId);
        app.Approve(reviewerId, "Looks good", Clock);
        await Db.SaveChangesAsync();

        var result = await _service.GetApplicationDetailAsync(app.Id);

        result.Should().NotBeNull();
        result.UserId.Should().Be(applicantId);
        result.UserDisplayName.Should().Be("Applicant");
        result.UserEmail.Should().Be("a@t.com");
        result.UserProfilePictureUrl.Should().Be("https://example.com/pic.png");
        result.ReviewerName.Should().Be("Admin");
        result.History.Should().NotBeEmpty();
    }

    [HumansFact]
    public async Task GetApplicationDetailAsync_NonExistent_ReturnsNull()
    {
        var result = await _service.GetApplicationDetailAsync(Guid.NewGuid());

        result.Should().BeNull();
    }

    [HumansFact]
    public async Task GetApplicationDetailAsync_NoOwnershipFilter()
    {
        var userId = Guid.NewGuid();
        var app = await SeedSubmittedApplicationAsync(userId, MembershipTier.Asociado);

        var result = await _service.GetApplicationDetailAsync(app.Id);

        result.Should().NotBeNull();
        result.Id.Should().Be(app.Id);
        result.UserId.Should().Be(userId);
    }

    // --- Helpers ---

    private async Task<MemberApplication> SeedSubmittedApplicationAsync(
        Guid userId,
        MembershipTier tier = MembershipTier.Colaborador)
    {
        var app = new MemberApplication
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            MembershipTier = tier,
            Motivation = "Motivation",
            SubmittedAt = Clock.GetCurrentInstant(),
            UpdatedAt = Clock.GetCurrentInstant()
        };
        Db.Applications.Add(app);
        await Db.SaveChangesAsync();
        return app;
    }
}
