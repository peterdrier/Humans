using AwesomeAssertions;
using Humans.Application.Services.Users.AccountLifecycle;
using Humans.AuditLog.Contracts;
using Humans.Auth.Contracts;
using Humans.Base.Interfaces;
using Humans.Base.Interfaces.Caching;
using Humans.Email.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Shifts.Contracts;
using Humans.Teams.Contracts;
using Humans.Tickets.Contracts;
using Humans.Users.Contracts;
using Humans.Users.Data.Repositories;
using Humans.Users.Services;
using Humans.Users.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Humans.Users.Tests.Services.Users;

/// <summary>
/// <see cref="AccountDeletionServiceTests"/> substitutes every contributor, which is
/// why a second identity collapse layered on top of the Account contributor's went
/// unnoticed (nobodies-collective/Humans#853). These run the orchestrator with the
/// real <see cref="UserService"/> as both <see cref="IUserService"/> and the Account
/// <see cref="IUserDataContributor"/>, over the harness's in-memory store, so the
/// row the purge leaves behind is asserted rather than assumed.
/// </summary>
public sealed class AccountDeletionServiceRealContributorTests : ServiceTestHarness
{
    private readonly UserService _userService;
    private readonly AccountDeletionService _service;
    private readonly ITeamService _teamService = Substitute.For<ITeamService>();
    private readonly ITicketServiceRead _ticketQueryService = Substitute.For<ITicketServiceRead>();

    public AccountDeletionServiceRealContributorTests()
    {
        var communicationPreferenceRepo = Substitute.For<ICommunicationPreferenceRepository>();
        communicationPreferenceRepo.GetByUserIdReadOnlyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CommunicationPreference>>([]));
        communicationPreferenceRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CommunicationPreference>>([]));

        _userService = new UserService(
            new UserRepository(DbFactory, Clock),
            communicationPreferenceRepo,
            AdminAuthorization,
            Substitute.For<IRoleAssignmentClaimsCacheInvalidator>(),
            Substitute.For<IFileStorage>(),
            Clock,
            NullLogger<UserService>.Instance);

        _ticketQueryService.GetUserTicketHoldingsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new UserTicketHoldings(0, []));

        // The real UserService cannot answer the merge-chain lookup (that primitive only
        // exists on the caching decorator), and these accounts have no merge history.
        var userServiceRead = Substitute.For<IUserServiceRead>();
        userServiceRead.GetMergedSourceIdsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<Guid>());

        _service = new AccountDeletionService(
            _userService,
            userServiceRead,
            Substitute.For<IUserEmailService>(),
            _teamService,
            Substitute.For<IRoleAssignmentService>(),
            [_userService],
            _ticketQueryService,
            Substitute.For<IUserInfoInvalidator>(),
            Substitute.For<IRoleAssignmentClaimsCacheInvalidator>(),
            Substitute.For<IShiftAuthorizationInvalidator>(),
            Substitute.For<IShiftViewInvalidator>(),
            Substitute.For<IAuditLogService>(),
            Substitute.For<IEmailService>(),
            Substitute.For<IEmailMessageFactory>(),
            Clock,
            NullLogger<AccountDeletionService>.Instance);
    }

    [HumansFact]
    public async Task PurgeAsync_CollapsesIdentityExactlyOnce()
    {
        var user = SeedUser(Guid.NewGuid(), "Real Human");
        Db.UserEmails.Add(new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Email = "real-human@example.com",
            IsVerified = true,
            IsPrimary = true,
        });
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.PurgeAsync(user.Id, ct: TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();

        ClearAllTrackers();
        var purged = await Db.Users.AsNoTracking()
            .FirstAsync(u => u.Id == user.Id, TestContext.Current.CancellationToken);

        // The GDPR sentinel exactly — a second collapse would have captured this
        // tombstone as "Purged (Deleted User)" and broken IsGdprAnonymized.
        purged.DisplayName.Should().Be(UserInfo.GdprAnonymizedBurnerName);
        purged.UserName.Should().Be($"deleted-{user.Id:N}@deleted.local");
        purged.LockoutEnd.Should().Be(DateTimeOffset.MaxValue);

        var remainingEmails = await Db.UserEmails.AsNoTracking()
            .Where(e => e.UserId == user.Id)
            .ToListAsync(TestContext.Current.CancellationToken);
        remainingEmails.Should().BeEmpty();
    }

    [HumansFact]
    public async Task AnonymizeExpiredAccountAsync_LeavesTheSameTombstoneAsPurge()
    {
        var user = SeedUser(Guid.NewGuid(), "Expired Human");
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var summary = await _service.AnonymizeExpiredAccountAsync(
            user.Id, TestContext.Current.CancellationToken);

        summary.Should().NotBeNull();
        summary.OriginalDisplayName.Should().Be("Expired Human");

        ClearAllTrackers();
        var anonymized = await Db.Users.AsNoTracking()
            .FirstAsync(u => u.Id == user.Id, TestContext.Current.CancellationToken);
        anonymized.DisplayName.Should().Be(UserInfo.GdprAnonymizedBurnerName);
    }
}
