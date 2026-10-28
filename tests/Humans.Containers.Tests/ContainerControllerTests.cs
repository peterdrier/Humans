using System.Security.Claims;
using AwesomeAssertions;
using Humans.Camps.Contracts;
using Humans.CityPlanning.Contracts;
using Humans.Containers.Contracts;
using Humans.Containers.Controllers;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Humans.Containers.Tests;

public sealed class ContainerControllerTests
{
    [HumansTheory]
    [InlineData("Create")]
    [InlineData("Edit")]
    [InlineData("Delete")]
    public async Task Writes_stop_at_the_user_lookup_when_the_request_is_cancelled(string action)
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var userId = Guid.NewGuid();
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(userId, cancellation.Token)
            .Returns(ValueTask.FromException<UserInfo?>(new OperationCanceledException(cancellation.Token)));
        var camps = Substitute.For<ICampService>();
        var containers = Substitute.For<IContainerService>();
        var controller = new ContainerController(
            camps, containers, Substitute.For<ICityPlanningService>(),
            Substitute.For<IAuthorizationService>(), users,
            Substitute.For<IStringLocalizer<ContainersResource>>(), NullLogger<ContainerController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test")),
                    RequestAborted = cancellation.Token,
                },
            },
        };

        Func<Task<IActionResult>> act = action switch
        {
            "Create" => () => controller.Create("camp", new ContainerFormModel { Name = "Container" }, cancellation.Token),
            "Edit" => () => controller.Edit("camp", Guid.NewGuid(), new ContainerFormModel { Name = "Container" }, cancellation.Token),
            "Delete" => () => controller.Delete("camp", Guid.NewGuid(), cancellation.Token),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };

        await act.Should().ThrowAsync<OperationCanceledException>();
        camps.ReceivedCalls().Should().BeEmpty();
        containers.ReceivedCalls().Should().BeEmpty();
    }
}
