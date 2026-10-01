using AwesomeAssertions;
using Humans.Base;
using Humans.Base.Configuration;
using Humans.Base.Enums;
using Humans.GoogleIntegration.Contracts;
using Humans.Teams.Contracts;
using Humans.Teams.Controllers;
using Humans.Teams.Models;
using Humans.Teams.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;

namespace Humans.Teams.Tests.Controllers;

public class TeamControllerPageContentTests
{
    [HumansFact]
    public async Task Details_RendersPageContentWithSharedMarkdownProtections()
    {
        const string markdown = """
            <p style="background-image:url(https://tracker.example/pixel)">Hello</p>
            <img src="http://insecure.example/pixel" />
            <img src="https://secure.example/photo" />

            - [x] Complete
            """;
        var team = new TeamPageTeamSummary(
            Guid.NewGuid(), "Test", "Test", null, "test", true, false, false,
            SystemTeamType.None, Instant.FromUtc(2026, 1, 1, 0, 0), true, false,
            markdown, [], null, null, null);
        var page = new TeamPageDetailResult(
            team, [], [], [], [], false, false, false, false, false, false, false,
            null, 0, null, null, [], []);
        var pages = Substitute.For<ITeamPageService>();
        pages.GetTeamPageDetailAsync("test", null, false, Arg.Any<CancellationToken>()).Returns(page);
        var controller = new TeamController(
            Substitute.For<ITeamManagementService>(), pages, Substitute.For<IUserServiceRead>(),
            Substitute.For<ITeamResourceService>(), Substitute.For<IStringLocalizer<TeamsResource>>(),
            Substitute.For<IStringLocalizer<SharedResource>>(), new ConfigurationBuilder().Build(),
            new ConfigurationRegistry(), SystemClock.Instance, Substitute.For<IAuthorizationService>(),
            NullLogger<TeamController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var result = await controller.Details("test", Xunit.TestContext.Current.CancellationToken);

        var model = result.Should().BeOfType<ViewResult>().Subject.Model
            .Should().BeOfType<TeamDetailViewModel>().Subject;
        model.PageContentHtml.Should().NotContain("style=").And.NotContain("http://insecure.example");
        model.PageContentHtml.Should().Contain("https://secure.example/photo");
        model.PageContentHtml.Should().Contain("type=\"checkbox\"").And.Contain("checked").And.Contain("disabled");
    }
}
