using System.Globalization;
using System.Security.Claims;
using Humans.Base.Constants;
using Humans.Teams.Domain;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
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
    [HumansTheory]
    [Xunit.InlineData("Join", false)]
    [Xunit.InlineData("Join", true)]
    [Xunit.InlineData("Leave", false)]
    [Xunit.InlineData("Leave", true)]
    [Xunit.InlineData("Withdraw", false)]
    [Xunit.InlineData("Withdraw", true)]
    public async Task MemberActionErrors_UseSpanishResourcesAndUnknownFailureFallback(string action, bool unknown)
    {
        var originalCulture = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("es");
        try
        {
            using var services = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
            var userId = Guid.NewGuid();
            var users = Substitute.For<IUserServiceRead>();
            users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
                .Returns(UserInfo.Create(new User { Id = userId }, [], [], [], null, []));
            var team = new Team { Id = Guid.NewGuid(), Name = "Alpha", Slug = "alpha" };
            var teams = Substitute.For<ITeamManagementService>();
            teams.GetTeamEntityBySlugAsync(team.Slug, Arg.Any<CancellationToken>()).Returns(team);
            var key = unknown ? "untranslated provider failure" : action switch
            {
                "Join" => "Team_AlreadyPendingRequest",
                "Leave" => "Teams_NotMember",
                _ => "Teams_RequestUnavailable"
            };
            teams.JoinTeamAsync(team.Id, userId, Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException<TeamJoinOutcome>(new InvalidOperationException(key)));
            teams.LeaveTeamAsync(team.Id, userId, Arg.Any<CancellationToken>())
                .Returns(Task.FromException<bool>(new InvalidOperationException(key)));
            teams.WithdrawJoinRequestAsync(Arg.Any<Guid>(), userId, Arg.Any<CancellationToken>())
                .Returns(Task.FromException(new InvalidOperationException(key)));
            var http = new DefaultHttpContext
            {
                RequestServices = services,
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"))
            };
            var controller = new TeamController(
                teams, Substitute.For<ITeamPageService>(), users,
                Substitute.For<ITeamResourceService>(), services.GetRequiredService<IStringLocalizer<TeamsResource>>(),
                services.GetRequiredService<IStringLocalizer<SharedResource>>(), new ConfigurationBuilder().Build(),
                new ConfigurationRegistry(), Substitute.For<IClock>(), Substitute.For<IAuthorizationService>(),
                NullLogger<TeamController>.Instance)
            {
                ControllerContext = new ControllerContext { HttpContext = http },
                TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>()),
                Url = Substitute.For<IUrlHelper>()
            };

            var result = action switch
            {
                "Join" => await controller.Join(team.Slug, new JoinTeamViewModel { TeamId = team.Id }),
                "Leave" => await controller.Leave(team.Slug),
                _ => await controller.WithdrawRequest(Guid.NewGuid())
            };

            result.Should().BeOfType<RedirectToActionResult>();
            controller.TempData[TempDataKeys.ErrorMessage].Should().Be(unknown
                ? "No se pudo completar esta acción del equipo. Inténtalo de nuevo."
                : action switch
                {
                    "Join" => "Ya tiene una solicitud pendiente para este equipo.",
                    "Leave" => "No formas parte de este equipo.",
                    _ => "Esta solicitud para unirte no está disponible o ya no está pendiente."
                });
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalCulture;
        }
    }

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
