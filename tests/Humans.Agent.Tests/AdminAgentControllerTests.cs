using AwesomeAssertions;
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
    public async Task Reload_reports_the_actual_refresh_outcome(bool succeeded)
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var preload = Substitute.For<IAgentPreloadCorpusBuilder>();
        preload.ReloadAllAsync(cancellationToken).Returns(succeeded);
        var context = new DefaultHttpContext();
        var controller = new AdminAgentController(
            Substitute.For<IAgentSettingsService>(), Substitute.For<IAgentService>(),
            Substitute.For<IAgentAdminStatusService>(), preload, Substitute.For<IUserServiceRead>())
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
