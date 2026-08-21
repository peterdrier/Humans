using Humans.Auth.Contracts;
using Humans.Users.Services;
using AwesomeAssertions;
using Humans.Base.Interfaces;
using Humans.AuditLog.Contracts;
using Humans.Base.Interfaces.Caching;
using Humans.Email.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Users.Contracts;
using Humans.Shifts.Contracts;
using Humans.Teams.Contracts;
using Humans.Tickets.Contracts;
using Humans.Application.Services.Users.AccountLifecycle;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Users.Tests.Services;

/// <summary>
/// Orchestration coverage for <see cref="IAccountDeletionService"/> — the
/// single entry point that replaced the cascade code formerly scattered
/// across <c>UserService</c>, <c>ProfileService</c>, and
/// <c>OnboardingService</c> (issue nobodies-collective/Humans#582). Verifies the order + side effects
/// of the three deletion paths: user-requested, admin-initiated, expiry.
/// </summary>
public class AccountDeletionServiceTests
{
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IUserEmailService _userEmailService = Substitute.For<IUserEmailService>();
    private readonly ITeamService _teamService = Substitute.For<ITeamService>();
    private readonly IRoleAssignmentService _roleAssignmentService = Substitute.For<IRoleAssignmentService>();
    private readonly IUserDataContributor _identityContributor = Substitute.For<IUserDataContributor>();
    private readonly IUserDataContributor _sectionContributor = Substitute.For<IUserDataContributor>();
    private readonly ITicketServiceRead _ticketQueryService = Substitute.For<ITicketServiceRead>();
    private readonly IRoleAssignmentClaimsCacheInvalidator _roleAssignmentClaimsInvalidator =
        Substitute.For<IRoleAssignmentClaimsCacheInvalidator>();
    private readonly IShiftAuthorizationInvalidator _shiftAuthorizationInvalidator =
        Substitute.For<IShiftAuthorizationInvalidator>();
    private readonly IShiftViewInvalidator _shiftViewInvalidator =
        Substitute.For<IShiftViewInvalidator>();
    private readonly IAuditLogService _auditLogService = Substitute.For<IAuditLogService>();
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();
    private readonly IEmailMessageFactory _emailMessages = Substitute.For<IEmailMessageFactory>();
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 3, 14, 12, 0));
    private readonly AccountDeletionService _service;

    public AccountDeletionServiceTests()
    {
        _userService.AnonymizeProfileForDeletionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new UserProfileAnonymizeResult(false, null, null));
        _ticketQueryService.GetUserTicketHoldingsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new UserTicketHoldings(0, []));

        // The contributor owning the Account section must erase last — the fan-out
        // orders off the declaration, so the fakes declare the two shapes.
        _identityContributor.ErasureDeclaration.Returns(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [GdprExportSections.Account] = "tombstone"
            });
        _sectionContributor.ErasureDeclaration.Returns(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [GdprExportSections.Issues] = null
            });

        _service = new AccountDeletionService(
            _userService,
            _userEmailService,
            _teamService,
            _roleAssignmentService,
            [_identityContributor, _sectionContributor],
            _ticketQueryService,
            _roleAssignmentClaimsInvalidator,
            _shiftAuthorizationInvalidator,
            _shiftViewInvalidator,
            _auditLogService,
            _emailService,
            _emailMessages,
            _clock,
            NullLogger<AccountDeletionService>.Instance);
    }

    // ==========================================================================
    // RequestDeletionAsync
    // ==========================================================================

    [HumansFact]
    public async Task RequestDeletionAsync_UnknownUser_ReturnsNotFound()
    {
        var userId = Guid.NewGuid();
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns((UserInfo?)null);

        var result = await _service.RequestDeletionAsync(userId, Xunit.TestContext.Current.CancellationToken);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("NotFound");
        await _teamService.DidNotReceiveWithAnyArgs()
            .RevokeAllMembershipsAsync(Guid.Empty, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task RequestDeletionAsync_AlreadyPending_ReturnsAlreadyPending()
    {
        var userId = Guid.NewGuid();
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(MakeUser(userId, deletionPending: true));

        var result = await _service.RequestDeletionAsync(userId, Xunit.TestContext.Current.CancellationToken);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("AlreadyPending");
        await _teamService.DidNotReceiveWithAnyArgs()
            .RevokeAllMembershipsAsync(Guid.Empty, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task RequestDeletionAsync_Valid_SetsDeletionPendingAndCascades()
    {
        var userId = Guid.NewGuid();
        var user = MakeUser(userId);
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(user);
        _teamService.RevokeAllMembershipsAsync(userId, Arg.Any<CancellationToken>()).Returns(3);
        _roleAssignmentService.RevokeAllActiveAsync(userId, Arg.Any<CancellationToken>()).Returns(1);
        _userEmailService.GetNotificationTargetEmailsAsync(
                Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(userId)),
                Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, string>());

        var result = await _service.RequestDeletionAsync(userId, Xunit.TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();

        var expectedScheduledFor = _clock.GetCurrentInstant().Plus(Duration.FromDays(30));
        await _userService.Received(1).SetDeletionPendingAsync(
            userId, _clock.GetCurrentInstant(), expectedScheduledFor,
            Arg.Any<Instant?>(), Arg.Any<CancellationToken>());

        await _teamService.Received(1).RevokeAllMembershipsAsync(userId, Arg.Any<CancellationToken>());
        await _roleAssignmentService.Received(1).RevokeAllActiveAsync(userId, Arg.Any<CancellationToken>());

        await _auditLogService.Received(1).LogAsync(
            AuditAction.MembershipsRevokedOnDeletionRequest, nameof(User), userId,
            Arg.Is<string>(s => s.Contains("3") && s.Contains("1")),
            userId,
            Arg.Any<Guid?>(), Arg.Any<string?>());

        _emailMessages.Received(1).AccountDeletionRequested(
            user.Email!, user.BurnerName,
            Arg.Any<Instant>(), user.PreferredLanguage);

        // Shift-authorization cache must drop in-orchestrator (parity with
        // PurgeAsync / AnonymizeExpiredAccountAsync) so direct callers don't
        // depend on the Profile caching decorator for correctness.
        _shiftAuthorizationInvalidator.Received(1).Invalidate(userId);
    }

    [HumansFact]
    public async Task RequestDeletionAsync_PrefersVerifiedNotificationEmailOverUserEmail()
    {
        var userId = Guid.NewGuid();
        var user = MakeUser(userId, email: "primary@example.com");
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(user);
        _userEmailService.GetNotificationTargetEmailsAsync(
                Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(userId)),
                Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, string> { [userId] = "notif@example.com" });

        await _service.RequestDeletionAsync(userId, Xunit.TestContext.Current.CancellationToken);

        _emailMessages.Received(1).AccountDeletionRequested(
            "notif@example.com", user.BurnerName,
            Arg.Any<Instant>(), user.PreferredLanguage);
    }

    [HumansFact]
    public async Task RequestDeletionAsync_TicketHolder_SetsEligibleAfterAndIsHeldForTicket()
    {
        // Ticket-hold path: deletion is held until after the event so the
        // ticket stays usable. Drives different UI copy in both Profile and
        // Guest deletion entry points, so the result must carry the hold date
        // and the IsHeldForTicket flag verbatim.
        var userId = Guid.NewGuid();
        var user = MakeUser(userId);
        var holdDate = _clock.GetCurrentInstant().Plus(Duration.FromDays(60));
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(user);
        _ticketQueryService.GetUserTicketHoldingsAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new UserTicketHoldings(
                1,
                [],
                HasCurrentEventTicket: true,
                PostEventHoldDate: holdDate));
        _userEmailService.GetNotificationTargetEmailsAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, string>());

        var result = await _service.RequestDeletionAsync(userId, Xunit.TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        result.IsHeldForTicket.Should().BeTrue();
        result.EffectiveDeletionDate.Should().Be(holdDate);

        await _userService.Received(1).SetDeletionPendingAsync(
            userId,
            _clock.GetCurrentInstant(),
            _clock.GetCurrentInstant().Plus(Duration.FromDays(30)),
            holdDate,
            Arg.Any<CancellationToken>());
    }

    // ==========================================================================
    // CancelDeletionAsync
    // ==========================================================================

    [HumansFact]
    public async Task CancelDeletionAsync_PendingDeletion_ClearsViaUserService()
    {
        var userId = Guid.NewGuid();
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(MakeUser(userId, deletionPending: true));

        var result = await _service.CancelDeletionAsync(userId, Xunit.TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        await _userService.Received(1).ClearDeletionAsync(userId, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CancelDeletionAsync_NoPendingDeletion_ReturnsNoDeletionPending()
    {
        var userId = Guid.NewGuid();
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(MakeUser(userId));

        var result = await _service.CancelDeletionAsync(userId, Xunit.TestContext.Current.CancellationToken);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("NoDeletionPending");
        await _userService.DidNotReceiveWithAnyArgs().ClearDeletionAsync(Guid.Empty, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CancelDeletionAsync_UnknownUser_ReturnsNotFound()
    {
        var userId = Guid.NewGuid();
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns((UserInfo?)null);

        var result = await _service.CancelDeletionAsync(userId, Xunit.TestContext.Current.CancellationToken);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("NotFound");
        await _userService.DidNotReceiveWithAnyArgs().ClearDeletionAsync(Guid.Empty, Arg.Any<CancellationToken>());
    }

    // ==========================================================================
    // PurgeAsync (admin-initiated)
    // ==========================================================================

    [HumansFact]
    public async Task PurgeAsync_UnknownUser_ReturnsNotFound()
    {
        var userId = Guid.NewGuid();
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns((UserInfo?)null);
        _userService.PurgeOwnDataAsync(userId, Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await _service.PurgeAsync(userId, ct: Xunit.TestContext.Current.CancellationToken);

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("NotFound");
        _teamService.DidNotReceive().InvalidateActiveTeamsCache();
        await _userService.DidNotReceiveWithAnyArgs().DeleteAllExternalLoginsForUserAsync(Guid.Empty, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task PurgeAsync_Success_ErasesEverySectionAndInvalidatesActiveTeamsCache()
    {
        var userId = Guid.NewGuid();
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(MakeUser(userId));
        _userService.PurgeOwnDataAsync(userId, Arg.Any<CancellationToken>()).Returns("Test Human");

        var result = await _service.PurgeAsync(userId, ct: Xunit.TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        // An admin purge must not erase less than the scheduled job does.
        await _sectionContributor.Received(1).EraseForUserAsync(userId, Arg.Any<CancellationToken>());
        await _identityContributor.Received(1).EraseForUserAsync(userId, Arg.Any<CancellationToken>());
        await _userService.Received(1).PurgeOwnDataAsync(userId, Arg.Any<CancellationToken>());
        await _userService.Received(1).DeleteAllExternalLoginsForUserAsync(userId, Arg.Any<CancellationToken>());
        _teamService.Received(1).InvalidateActiveTeamsCache();
        // Parity with AnonymizeExpiredAccountAsync: per-user caches that key
        // off identity must also drop on admin purge.
        _roleAssignmentClaimsInvalidator.Received(1).Invalidate(userId);
        _shiftAuthorizationInvalidator.Received(1).Invalidate(userId);
    }

    [HumansFact]
    public async Task PurgeAsync_Success_WritesAuditLogWithActorAndDisplayName()
    {
        var userId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(MakeUser(userId));
        _userService.PurgeOwnDataAsync(userId, Arg.Any<CancellationToken>()).Returns("Test Human");

        await _service.PurgeAsync(userId, actorId, Xunit.TestContext.Current.CancellationToken);

        // GDPR right-of-access depends on this audit row surviving the purge.
        await _auditLogService.Received(1).LogAsync(
            AuditAction.AccountPurged, nameof(User), userId,
            Arg.Is<string>(s => s.Contains("Test Human")),
            actorId,
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    // ==========================================================================
    // AnonymizeExpiredAccountAsync
    // ==========================================================================

    [HumansFact]
    public async Task AnonymizeExpiredAccountAsync_UnknownUser_ReturnsNull()
    {
        var userId = Guid.NewGuid();
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns((UserInfo?)null);

        var result = await _service.AnonymizeExpiredAccountAsync(userId, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeNull();
        await _teamService.DidNotReceiveWithAnyArgs().RevokeAllMembershipsAsync(Guid.Empty, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task AnonymizeExpiredAccountAsync_ErasesEverySectionAndInvalidatesCaches()
    {
        var userId = Guid.NewGuid();
        var user = MakeUser(userId, email: "expired@example.com", displayName: "Expired Human",
            preferredLanguage: "es");

        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(user);

        var result = await _service.AnonymizeExpiredAccountAsync(userId, Xunit.TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result.OriginalEmail.Should().Be("expired@example.com");
        result.OriginalDisplayName.Should().Be("Expired Human");
        result.PreferredLanguage.Should().Be("es");

        await _sectionContributor.Received(1).EraseForUserAsync(userId, Arg.Any<CancellationToken>());
        await _identityContributor.Received(1).EraseForUserAsync(userId, Arg.Any<CancellationToken>());

        _teamService.Received(1).RemoveMemberFromAllTeamsCache(userId);
        _roleAssignmentClaimsInvalidator.Received(1).Invalidate(userId);
        _shiftAuthorizationInvalidator.Received(1).Invalidate(userId);
    }

    [HumansFact]
    public async Task AnonymizeExpiredAccountAsync_ErasesTheAccountIdentityLast()
    {
        // Sections that must reach an external processor (the Workspace suspend)
        // need the human's addresses, which the Account contributor is about to drop.
        var userId = Guid.NewGuid();
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(MakeUser(userId));

        var order = new List<string>();
        _sectionContributor.EraseForUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns(_ => { order.Add("section"); return Task.CompletedTask; });
        _identityContributor.EraseForUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns(_ => { order.Add("identity"); return Task.CompletedTask; });

        await _service.AnonymizeExpiredAccountAsync(userId, Xunit.TestContext.Current.CancellationToken);

        order.Should().Equal("section", "identity");
    }

    [HumansFact]
    public async Task AnonymizeExpiredAccountAsync_ContributorFailurePreservesDeletionFields()
    {
        // A throwing contributor must abort the run: the Account contributor never
        // clears DeletionScheduledFor, so tomorrow's job retries the whole fan-out.
        var userId = Guid.NewGuid();
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(MakeUser(userId));
        _sectionContributor.EraseForUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("boom"));

        var act = () => _service.AnonymizeExpiredAccountAsync(userId, Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
        await _identityContributor.DidNotReceive().EraseForUserAsync(userId, Arg.Any<CancellationToken>());
    }

    // ==========================================================================
    // Helpers
    // ==========================================================================

    private static UserInfo MakeUser(
        Guid userId,
        string? email = "test@example.com",
        string displayName = "Test Human",
        string preferredLanguage = "en",
        bool deletionPending = false)
    {
        var user = new User
        {
            Id = userId,
            Email = email,
            UserName = email,
            DisplayName = displayName,
            PreferredLanguage = preferredLanguage,
        };
        if (deletionPending)
        {
            var now = Instant.FromUtc(2026, 3, 14, 12, 0);
            user.DeletionRequestedAt = now;
            user.DeletionScheduledFor = now.Plus(Duration.FromDays(30));
        }
        return UserInfoFactory.Create(user, [], [], [], null, [], [], [], []);
    }

}
