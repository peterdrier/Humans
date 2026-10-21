using Humans.Users.Tests.Infrastructure;
using Humans.Governance.Contracts;
using AwesomeAssertions;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using Humans.Users.ViewComponents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using NSubstitute;

namespace Humans.Users.Tests.ViewComponents;

public sealed class HumanSummaryViewComponentTests
{
    [HumansFact]
    public async Task InvokeAsync_RequestAborted_PropagatesCancellation()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(Arg.Any<Guid>(), aborted.Token)
            .Returns(_ => ValueTask.FromException<UserInfo?>(new OperationCanceledException(aborted.Token)));
        var component = new HumanSummaryViewComponent(users, Substitute.For<ITeamService>(), Substitute.For<IGovernanceService>(), Substitute.For<IMembershipCalculator>())
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext
                {
                    HttpContext = new DefaultHttpContext { RequestAborted = aborted.Token }
                }
            }
        };

        var act = () => component.InvokeAsync(Guid.NewGuid());

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [HumansFact]
    public async Task InvokeAsync_MembershipStatus_IsTheCalculatorsStatus()
    {
        var userId = Guid.NewGuid();
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(new User { Id = userId, BurnerName = "Ada" }
            .ToUserInfo(profile: new Profile { Id = Guid.NewGuid(), UserId = userId, BurnerName = "Ada" }));
        var teams = Substitute.For<ITeamService>();
        teams.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, TeamInfo>());
        var calculator = Substitute.For<IMembershipCalculator>();
        calculator.GetMembershipSnapshotAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new MembershipSnapshot(MembershipStatus.Inactive, true, 1, 1, [Guid.NewGuid()]));
        var component = new HumanSummaryViewComponent(users, teams, Substitute.For<IGovernanceService>(), calculator)
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext { HttpContext = new DefaultHttpContext() }
            }
        };

        var result = await component.InvokeAsync(userId);

        var model = result.Should().BeOfType<ViewViewComponentResult>().Subject.ViewData!.Model
            .Should().BeOfType<Humans.Users.Models.ProfileSummaryViewModel>().Subject;
        model.MembershipStatus.Should().Be(nameof(MembershipStatus.Inactive));
    }
}
