using System.Security.Claims;
using AwesomeAssertions;
using Humans.Auth.Contracts;
using Humans.Base.Authorization;
using Humans.Base.Constants;
using Humans.Governance.Controllers;
using Humans.Governance.Services;
using Humans.Testing;
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

    private GovernanceBoardSeats Seats() => new(_roles, _write, new FakeClock(Now));

    private static RoleAssignmentSummarySnapshot Row(Guid userId, string role, Instant? validTo = null) =>
        new(Guid.NewGuid(), userId, null, "Member", role, Now - Duration.FromDays(30), validTo, null, Guid.NewGuid(), null, Now);

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
        var controller = new GovernanceController(users, Substitute.For<IGovernanceIndexService>(), Seats(), authorization,
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
