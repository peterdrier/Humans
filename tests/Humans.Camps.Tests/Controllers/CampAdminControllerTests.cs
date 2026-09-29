using System.Security.Claims;
using AwesomeAssertions;
using Humans.Camps.Models;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;

namespace Humans.Camps.Tests.Controllers;

public class CampAdminControllerTests
{
    [HumansFact]
    public async Task DeactivateRole_PropagatesRequestCancellation()
    {
        var camps = Substitute.For<ICampService>();
        var roles = Substitute.For<ICampRoleService>();
        var users = Substitute.For<IUserServiceRead>();
        var actorId = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        users.GetUserInfoAsync(actorId, cancellation.Token)
            .Returns(new ValueTask<UserInfo?>(MakeUser(actorId)));
        roles.DeactivateDefinitionAsync(Arg.Any<Guid>(), actorId, cancellation.Token)
            .Returns(Task.FromCanceled<bool>(cancellation.Token));

        var controller = CreateController(camps, roles, users, actorId);

        var act = () => controller.DeactivateRole(Guid.NewGuid(), cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static CampAdminController CreateController(
        ICampService camps, ICampRoleService roles, IUserServiceRead users, Guid actorId)
    {
        var controller = new CampAdminController(
            camps,
            roles,
            new CampAdminPageBuilder(camps, roles),
            new CampCsvExportBuilder(camps, users),
            users,
            NullLogger<CampAdminController>.Instance);
        var services = new ServiceCollection().BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, actorId.ToString())], authenticationType: "test"));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = http,
            ActionDescriptor = new ControllerActionDescriptor { ActionName = nameof(CampAdminController.DeactivateRole) },
        };
        controller.TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>());
        return controller;
    }

    private static UserInfo MakeUser(Guid id) => new(
        id, "actor", false, "en", null, SystemClock.Instance.GetCurrentInstant(), null, null,
        null, null, null, false, false, null, null, null, null, null, null, [], [], [], null, []);
}
