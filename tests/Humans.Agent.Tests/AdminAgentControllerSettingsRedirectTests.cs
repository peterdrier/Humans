using AwesomeAssertions;
using Humans.Agent.Controllers;
using Humans.Agent.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Humans.Agent.Tests;

/// <summary>
/// Superseded by the /Settings#agent tab (peterdrier/Humans#1634) — one canonical URL
/// per page, so a GET here always redirects rather than staying a second live page.
/// </summary>
public sealed class AdminAgentControllerSettingsRedirectTests
{
    [HumansFact]
    public void Settings_Get_RedirectsToTheSettingsPageAgentTab()
    {
        var controller = new AdminAgentController(
            Substitute.For<IAgentSettingsService>(),
            Substitute.For<IAgentService>(),
            Substitute.For<IAgentAdminStatusService>(),
            Substitute.For<IAgentPreloadCorpusBuilder>(),
            Substitute.For<IUserServiceRead>());

        var result = controller.Settings();

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/Settings#agent");
    }
}
