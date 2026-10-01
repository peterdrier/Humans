using AwesomeAssertions;
using Humans.Auth.Contracts;
using Humans.Base.Enums;
using Humans.Governance.Contracts;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using Humans.Users.Models;
using Humans.Users.ViewComponents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using NSubstitute;

namespace Humans.Users.Tests.ViewComponents;

public sealed class ProfileCardViewComponentTests
{
    [HumansFact]
    public async Task InvokeAsync_RequestAborted_PropagatesCancellation()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var users = Substitute.For<IUserServiceRead>();
        users.GetUserInfoAsync(Arg.Any<Guid>(), aborted.Token)
            .Returns(_ => ValueTask.FromException<UserInfo?>(new OperationCanceledException(aborted.Token)));
        var component = new ProfileCardViewComponent(
            users, Substitute.For<IContactFieldService>(), Substitute.For<IUserEmailService>(),
            Substitute.For<ITeamServiceRead>(), Substitute.For<IRoleAssignmentService>(),
            Substitute.For<IMembershipCalculatorRead>(), Substitute.For<ICommunicationPreferenceService>())
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext
                {
                    HttpContext = new DefaultHttpContext { RequestAborted = aborted.Token }
                }
            }
        };

        var act = () => component.InvokeAsync(Guid.NewGuid(), ProfileCardViewMode.Self);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
