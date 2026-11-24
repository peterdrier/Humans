using System.Security.Claims;
using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Auth.Contracts;
using Humans.Governance.Data;
using Humans.Google.Contracts;
using Humans.Governance.Domain;
using Microsoft.EntityFrameworkCore;
using Humans.Base.Authorization;
using Humans.Base.Constants;
using Humans.Governance.Controllers;
using Humans.Governance.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Governance.Tests.Services;

public sealed class GovernanceBoardSeatsTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 11, 19, 12, 0);
    private readonly IRoleAssignmentService _roles = Substitute.For<IRoleAssignmentService>();
    private readonly IRoleAssignmentWrite _write = Substitute.For<IRoleAssignmentWrite>();

    private readonly IUserService _users = Substitute.For<IUserService>();

    private readonly GovernanceBodyRepository _repo = new(new TestDbContextFactory<GovernanceDbContext>(
        new DbContextOptionsBuilder<GovernanceDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options));

    private readonly IGoogleGroupSync _groupSync = Substitute.For<IGoogleGroupSync>();

    private GovernanceBoardSeats Seats() =>
        new(_roles, _write, _users, _repo, _audit,
            new GovernanceBodySync(_repo, _groupSync, Substitute.For<IGoogleSyncWrite>()), new FakeClock(Now));

    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();

    private void OnBoard(params Guid[] userIds)
    {
        _roles.GetActiveUserIdsInRoleAsync(RoleNames.Board, Arg.Any<CancellationToken>()).Returns(userIds);
        _roles.IsUserBoardMemberAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci => userIds.Contains(ci.Arg<Guid>()));
    }

    [HumansFact]
    public async Task An_office_moves_to_its_new_holder_who_gives_up_their_old_one()
    {
        var (ana, ben) = (Guid.NewGuid(), Guid.NewGuid());
        OnBoard(ana, ben);
        await Seats().SetPositionAsync(BoardPosition.President, ana, Guid.NewGuid());
        await Seats().SetPositionAsync(BoardPosition.Treasurer, ben, Guid.NewGuid());

        await Seats().SetPositionAsync(BoardPosition.President, ben, Guid.NewGuid());

        (await Seats().GetPositionsAsync()).Should().BeEquivalentTo(
            new Dictionary<Guid, BoardPosition> { [ben] = BoardPosition.President });
        await _audit.Received(1).LogAsync(GovernanceAuditActions.BoardPositionSet, Arg.Any<string>(), ana,
            "Board office President removed", Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<string?>());
        await _audit.Received(1).LogAsync(GovernanceAuditActions.BoardPositionSet, Arg.Any<string>(), ben,
            "Board office President given (gave up Treasurer)", Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task Clearing_empties_the_members_office_and_is_a_no_op_without_one()
    {
        var ana = Guid.NewGuid();
        OnBoard(ana);
        (await Seats().ClearPositionAsync(ana, Guid.NewGuid())).Success.Should().BeTrue();
        await Seats().SetPositionAsync(BoardPosition.Secretary, ana, Guid.NewGuid());

        await Seats().ClearPositionAsync(ana, Guid.NewGuid());

        (await _repo.GetBoardPositionsAsync()).Should().BeEmpty();
        await _audit.Received(1).LogAsync(GovernanceAuditActions.BoardPositionSet, Arg.Any<string>(), ana,
            "Board office Secretary removed", Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task Only_a_current_Board_member_can_hold_an_office_and_ending_the_seat_drops_it()
    {
        var (ana, outsider) = (Guid.NewGuid(), Guid.NewGuid());
        OnBoard(ana);
        (await Seats().SetPositionAsync(BoardPosition.Secretary, outsider, Guid.NewGuid())).ErrorKey
            .Should().Be(GovernanceBoardSeats.NotOnBoard);
        await Seats().SetPositionAsync(BoardPosition.Secretary, ana, Guid.NewGuid());
        var seat = Row(ana, RoleNames.Board);
        _roles.GetByUserIdAsync(ana, Arg.Any<CancellationToken>()).Returns([seat]);
        _write.EndRoleAsync(seat.Id, Arg.Any<Guid>(), null, Arg.Any<CancellationToken>()).Returns(new RoleAssignmentResult(true));

        await Seats().EndAsync(ana, Guid.NewGuid());

        (await _repo.GetBoardPositionsAsync()).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Merge_moves_the_office_unless_the_survivor_holds_one()
    {
        var (from, to, other) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await _repo.SetBoardPositionAsync(BoardPosition.Secretary, from, Now);
        await Seats().ReassignAsync(from, to, Guid.NewGuid(), Now, Xunit.TestContext.Current.CancellationToken);
        await _repo.SetBoardPositionAsync(BoardPosition.Treasurer, other, Now);
        await Seats().ReassignAsync(other, to, Guid.NewGuid(), Now, Xunit.TestContext.Current.CancellationToken);

        (await _repo.GetBoardPositionsAsync()).Select(p => (p.Position, p.UserId))
            .Should().BeEquivalentTo([(BoardPosition.Secretary, to)]);
    }

    [HumansFact]
    public async Task Add_refuses_an_unknown_account()
    {
        (await Seats().AddAsync(Guid.NewGuid(), Guid.NewGuid())).ErrorKey.Should().Be(GovernanceBoardSeats.InactiveAccount);

        _write.ReceivedCalls().Should().BeEmpty();
    }

    private static RoleAssignmentSummarySnapshot Row(Guid userId, string role, Instant? validTo = null) =>
        new(Guid.NewGuid(), userId, null, "Member", role, Now - Duration.FromDays(30), validTo, null, Guid.NewGuid(), null);

    [HumansFact]
    public async Task End_ends_only_the_active_Board_seat()
    {
        var userId = Guid.NewGuid();
        var board = Row(userId, RoleNames.Board);
        _roles.GetByUserIdAsync(userId, Arg.Any<CancellationToken>()).Returns(
            [Row(userId, "TeamsAdmin"), Row(userId, RoleNames.Board, Now - Duration.FromDays(1)), board]);
        _write.EndRoleAsync(board.Id, Arg.Any<Guid>(), null, Arg.Any<CancellationToken>()).Returns(new RoleAssignmentResult(true));

        (await Seats().EndAsync(userId, Guid.NewGuid())).Success.Should().BeTrue();

        await _write.Received(1).EndRoleAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _write.Received(1).EndRoleAsync(board.Id, Arg.Any<Guid>(), null, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task End_refuses_someone_without_a_Board_seat()
    {
        var userId = Guid.NewGuid();
        _roles.GetByUserIdAsync(userId, Arg.Any<CancellationToken>()).Returns([Row(userId, "TeamsAdmin")]);

        (await Seats().EndAsync(userId, Guid.NewGuid())).Success.Should().BeFalse();

        await _write.DidNotReceive().EndRoleAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public async Task Add_and_end_are_forbidden_without_role_assignment_rights(bool add)
    {
        var actorId = Guid.NewGuid();
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(actorId, Arg.Any<CancellationToken>()).Returns(new ValueTask<UserInfo?>(
            UserInfoFixtures.FromUser(new User { Id = actorId, DisplayName = "Board member", Email = "b@example.com" })));
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), RoleNames.Board, PolicyNames.RoleAssignmentManage)
            .Returns(AuthorizationResult.Failed());
        var controller = new GovernanceController(users, Substitute.For<IGovernanceIndexService>(), Seats(),
            new GovernanceBodyLinksService(_repo, Substitute.For<Humans.Teams.Contracts.ITeamService>(), Substitute.For<ITeamResourceService>(),
                _audit, new GovernanceBodySync(_repo, _groupSync, Substitute.For<IGoogleSyncWrite>()), new FakeClock(Now)),
            authorization,
            Substitute.For<IStringLocalizer<GovernanceResource>>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actorId.ToString())], "Test")),
                },
            },
        };

        var result = add ? await controller.AddBoardMember(Guid.NewGuid()) : await controller.EndBoardMember(Guid.NewGuid());

        result.Should().BeOfType<ForbidResult>();
        _write.ReceivedCalls().Should().BeEmpty();
    }
}
