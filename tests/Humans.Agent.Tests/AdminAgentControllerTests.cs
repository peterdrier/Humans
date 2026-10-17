using AwesomeAssertions;
using Humans.Agent.Domain;
using Humans.Agent.Models;
using System.Security.Claims;
using Humans.Agent.Controllers;
using Humans.Agent.Services;
using Humans.Base.Constants;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using NSubstitute;

namespace Humans.Agent.Tests;

public sealed class AdminAgentControllerTests
{
    [HumansTheory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public async Task Settings_requires_and_forwards_the_resolved_actor(bool actorExists)
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var actorId = Guid.NewGuid();
        var users = Substitute.For<IUserService>();
        if (actorExists)
            users.GetUserInfoAsync(actorId, ct).Returns(UserInfo.Create(
                new User { Id = actorId, BurnerName = "Admin" }, [], [], [], null, []));
        var settings = Substitute.For<IAgentSettingsService>();
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, actorId.ToString())], "test"))
        };
        var controller = new AdminAgentController(settings, Substitute.For<IAgentService>(),
            Substitute.For<IAgentAdminStatusService>(), Substitute.For<IAgentPreloadCorpusBuilder>(), users)
        {
            ControllerContext = new ControllerContext { HttpContext = context },
            TempData = new TempDataDictionary(context, Substitute.For<ITempDataProvider>())
        };

        var result = await controller.Settings(new AdminAgentSettingsViewModel(), ct);

        if (actorExists)
        {
            result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/Settings#agent");
            await settings.Received(1).UpdateAsync(Arg.Any<Action<AgentSettings>>(), actorId, ct);
        }
        else
        {
            result.Should().BeOfType<UnauthorizedResult>();
            settings.ReceivedCalls().Should().BeEmpty();
        }
    }

    [HumansTheory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public async Task Reload_reports_the_actual_refresh_outcome(bool succeeded)
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var preload = Substitute.For<IAgentPreloadCorpusBuilder>();
        preload.ReloadAllAsync(cancellationToken).Returns(succeeded);
        var context = new DefaultHttpContext();
        var controller = new AdminAgentController(
            Substitute.For<IAgentSettingsService>(), Substitute.For<IAgentService>(),
            Substitute.For<IAgentAdminStatusService>(), preload, Substitute.For<IUserService>())
        {
            ControllerContext = new ControllerContext { HttpContext = context },
            TempData = new TempDataDictionary(context, Substitute.For<ITempDataProvider>())
        };

        var result = await controller.ReloadKnowledgeBase(cancellationToken);

        result.Should().BeOfType<RedirectToActionResult>().Which.ActionName.Should().Be(nameof(AdminAgentController.Status));
        controller.TempData.Should().ContainKey(succeeded ? TempDataKeys.SuccessMessage : TempDataKeys.ErrorMessage);
        controller.TempData.Should().NotContainKey(succeeded ? TempDataKeys.ErrorMessage : TempDataKeys.SuccessMessage);
        await preload.Received(1).ReloadAllAsync(cancellationToken);
    }
}
