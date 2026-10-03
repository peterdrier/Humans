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
using Microsoft.Extensions.Logging;
using NSubstitute.ExceptionExtensions;
using Xunit;
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

    [HumansTheory]
    [InlineData("Approve")]
    [InlineData("Reject")]
    [InlineData("Reactivate")]
    [InlineData("Delete")]
    public async Task LifecycleAction_ExpectedRejection_LogsWarningWithoutException(string action)
    {
        var camps = Substitute.For<ICampService>();
        var users = Substitute.For<IUserServiceRead>();
        var actorId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var logger = Substitute.For<ILogger<CampAdminController>>();
        users.GetUserInfoAsync(actorId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(MakeUser(actorId)));
        var reason = string.Equals(action, "Delete", StringComparison.Ordinal) ? "Camp not found." : "Season not found.";
        ConfigureFailure(camps, action, targetId, actorId, new InvalidOperationException(reason));
        var controller = CreateController(camps, Substitute.For<ICampRoleService>(), users, actorId, logger);

        var result = await InvokeAction(controller, action, targetId);

        result.Should().BeOfType<RedirectToActionResult>().Which.ActionName.Should().Be("Index");
        controller.TempData["ErrorMessage"].Should().Be(reason);
        var args = logger.ReceivedCalls().Should().ContainSingle().Subject.GetArguments();
        args[0].Should().Be(LogLevel.Warning);
        args[3].Should().BeNull();
        args[2]!.ToString().Should().Contain(targetId.ToString()).And.Contain(reason);
        if (action is "Approve" or "Reject") args[2]!.ToString().Should().Contain(actorId.ToString());
    }

    [HumansTheory]
    [InlineData("Approve")]
    [InlineData("Reject")]
    [InlineData("Reactivate")]
    [InlineData("Delete")]
    public async Task LifecycleAction_UnexpectedFailure_Propagates(string action)
    {
        var camps = Substitute.For<ICampService>();
        var users = Substitute.For<IUserServiceRead>();
        var actorId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        users.GetUserInfoAsync(actorId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(MakeUser(actorId)));
        ConfigureFailure(camps, action, targetId, actorId, new IOException("Storage unavailable"));
        var controller = CreateController(camps, Substitute.For<ICampRoleService>(), users, actorId);

        var act = () => InvokeAction(controller, action, targetId);

        await act.Should().ThrowAsync<IOException>().WithMessage("Storage unavailable");
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RoleForm_ExpectedRuleRejection_LogsWarningAndRetainsForm(bool edit)
    {
        var roles = Substitute.For<ICampRoleService>();
        var users = Substitute.For<IUserServiceRead>();
        var actorId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        users.GetUserInfoAsync(actorId, Arg.Any<CancellationToken>()).Returns(new ValueTask<UserInfo?>(MakeUser(actorId)));
        var reason = edit ? "Camp role 'Worker' is a system role; only SlotCount and Description can be changed."
            : "A camp role definition with slug 'worker' already exists.";
        var failure = new InvalidOperationException(reason);
        roles.CreateDefinitionAsync(Arg.Any<CreateCampRoleDefinitionInput>(), actorId, Arg.Any<CancellationToken>()).ThrowsAsync(failure);
        roles.UpdateDefinitionAsync(roleId, Arg.Any<UpdateCampRoleDefinitionInput>(), actorId, Arg.Any<CancellationToken>()).ThrowsAsync(failure);
        var logger = Substitute.For<ILogger<CampAdminController>>();
        var controller = CreateController(Substitute.For<ICampService>(), roles, users, actorId, logger);
        var form = new CampRoleDefinitionFormViewModel { Name = "Worker", Slug = "worker" };
        var ct = Xunit.TestContext.Current.CancellationToken;

        var result = edit ? await controller.EditRole(roleId, form, ct) : await controller.CreateRole(form, ct);

        result.Should().BeOfType<ViewResult>().Which.Model.Should().BeSameAs(form);
        controller.ModelState[string.Empty]!.Errors.Should().ContainSingle().Which.ErrorMessage.Should().Be(reason);
        var args = logger.ReceivedCalls().Should().ContainSingle().Subject.GetArguments();
        args[0].Should().Be(LogLevel.Warning);
        args[3].Should().BeNull();
        args[2]!.ToString().Should().Contain(actorId.ToString()).And.Contain(reason);
        if (edit) args[2]!.ToString().Should().Contain(roleId.ToString());
    }

    [HumansFact]
    public async Task EditRole_UnexpectedResult_LogsErrorAndRetainsForm()
    {
        var roles = Substitute.For<ICampRoleService>();
        var users = Substitute.For<IUserServiceRead>();
        var actorId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        users.GetUserInfoAsync(actorId, Arg.Any<CancellationToken>()).Returns(new ValueTask<UserInfo?>(MakeUser(actorId)));
        roles.UpdateDefinitionAsync(roleId, Arg.Any<UpdateCampRoleDefinitionInput>(), actorId, Arg.Any<CancellationToken>())
            .Returns(new UpdateCampRoleDefinitionResult((UpdateCampRoleDefinitionStatus)99, string.Empty));
        var logger = Substitute.For<ILogger<CampAdminController>>();
        var controller = CreateController(Substitute.For<ICampService>(), roles, users, actorId, logger);
        var form = new CampRoleDefinitionFormViewModel { Name = "Worker", Slug = "worker" };

        var result = await controller.EditRole(roleId, form, Xunit.TestContext.Current.CancellationToken);

        result.Should().BeOfType<ViewResult>().Which.Model.Should().BeSameAs(form);
        controller.ModelState[string.Empty]!.Errors.Should().ContainSingle().Which.ErrorMessage.Should().Be("Failed to update role definition.");
        var args = logger.ReceivedCalls().Should().ContainSingle().Subject.GetArguments();
        args[0].Should().Be(LogLevel.Error);
        args[3].Should().BeOfType<System.Diagnostics.UnreachableException>().Which.Message.Should().Contain("99");
        args[2]!.ToString().Should().Contain(roleId.ToString());
    }

    private static void ConfigureFailure(ICampService camps, string action, Guid targetId, Guid actorId, Exception failure)
    {
        switch (action)
        {
            case "Approve": camps.ApproveSeasonAsync(targetId, actorId, null).ThrowsAsync(failure); break;
            case "Reject": camps.RejectSeasonAsync(targetId, actorId, "Rejected").ThrowsAsync(failure); break;
            case "Reactivate": camps.ReactivateSeasonAsync(targetId).ThrowsAsync(failure); break;
            case "Delete": camps.DeleteCampAsync(targetId).ThrowsAsync(failure); break;
            default: throw new ArgumentOutOfRangeException(nameof(action));
        }
    }

    private static Task<IActionResult> InvokeAction(CampAdminController controller, string action, Guid targetId) => action switch
    {
        "Approve" => controller.Approve(targetId, null),
        "Reject" => controller.Reject(targetId, "Rejected"),
        "Reactivate" => controller.Reactivate(targetId, null),
        "Delete" => controller.Delete(targetId),
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    private static CampAdminController CreateController(
        ICampService camps, ICampRoleService roles, IUserServiceRead users, Guid actorId, ILogger<CampAdminController>? logger = null)
    {
        var controller = new CampAdminController(
            camps,
            roles,
            new CampAdminPageBuilder(camps, roles),
            new CampCsvExportBuilder(camps, users),
            users,
            logger ?? NullLogger<CampAdminController>.Instance);
        var services = new ServiceCollection().BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, actorId.ToString())], authenticationType: "test"));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = http,
            ActionDescriptor = new ControllerActionDescriptor { ActionName = nameof(CampAdminController.DeactivateRole) },
        };
        controller.Url = Substitute.For<IUrlHelper>();
        controller.TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>());
        return controller;
    }

    private static UserInfo MakeUser(Guid id) => new(
        id, "actor", false, "en", null, SystemClock.Instance.GetCurrentInstant(), null, null,
        null, null, null, false, false, null, null, null, null, null, null, [], [], [], null, []);
}
