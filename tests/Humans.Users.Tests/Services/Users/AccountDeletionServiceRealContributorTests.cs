using AwesomeAssertions;
using Humans.Users.Data;
using Humans.Application.Services.Users.AccountLifecycle;
using Humans.AuditLog.Contracts;
using Humans.Auth.Contracts;
using Humans.Base.Interfaces;
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
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Xunit;

namespace Humans.Users.Tests.Services.Users;

/// <summary>
/// Runs the orchestrator over the real <see cref="UserService"/> as both <see cref="IUserManagement"/>
/// and the Account <see cref="IUserDataContributor"/>, so the row the purge leaves is asserted rather
/// than assumed: the all-substitute suite let a second identity collapse go unnoticed.
/// </summary>
public sealed class AccountDeletionServiceRealContributorTests : ServiceTestHarness
{
    private readonly UserService _userService;
    private readonly AccountDeletionService _service;
    private readonly ITeamWrite _teamService = Substitute.For<ITeamWrite>();
    private readonly ITicketService _ticketQueryService = Substitute.For<ITicketService>();

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
            Substitute.For<IFileStorage>(),
            Clock,
            NullLogger<UserService>.Instance, AuditLog);

        _ticketQueryService.GetUserTicketHoldingsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new UserTicketHoldings(0, []));

        // Gdpr's real erasure loop is internal to Humans.Gdpr, so a forwarding substitute runs the
        // real UserService Account contributor for the id: what that contributor leaves on the
        // row is the point, not the trivial loop that drives it.
        var gdprService = Substitute.For<IGdprService>();
        gdprService.EraseForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci => (_userService)
                .EraseForUserAsync(ci.Arg<Guid>(), ci.Arg<CancellationToken>()));

        _service = new AccountDeletionService(
            _userService,
            Substitute.For<IUserEmailService>(),
            _teamService,
            Substitute.For<IRoleAssignmentWrite>(),
            gdprService,
            _ticketQueryService,
            Substitute.For<IUserInfoSliceRefresher>(),
            Substitute.For<IAuditLogService>(),
            Substitute.For<IEmailService>(),
            TestUsersEmails.Create(),
            Clock,
            Substitute.For<IHostEnvironment>(),
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
        user.BurnerName = "Expired Human";
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var summary = await _service.AnonymizeExpiredAccountAsync(
            user.Id, TestContext.Current.CancellationToken);

        summary.Should().NotBeNull();
        summary.OriginalBurnerName.Should().Be("Expired Human");

        ClearAllTrackers();
        var anonymized = await Db.Users.AsNoTracking()
            .FirstAsync(u => u.Id == user.Id, TestContext.Current.CancellationToken);
        anonymized.DisplayName.Should().Be(UserInfo.GdprAnonymizedBurnerName);
    }
}
