using System.Globalization;
using System.Security.Claims;
using Humans.Base.Constants;
using Humans.Base.Extensions;
using Humans.Teams.Domain;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
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
    [Xunit.InlineData("birthdays", "viewer")]
    [Xunit.InlineData("my", "viewer")]
    [Xunit.InlineData("join", "viewer")]
    [Xunit.InlineData("join", "entity")]
    [Xunit.InlineData("join", "info")]
    [Xunit.InlineData("join", "pending")]
    public async Task Member_gets_cancel_at_each_read_boundary(string action, string boundary)
    {
        using var services = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
        using var request = new CancellationTokenSource();
        var userId = Guid.NewGuid();
        var users = Substitute.For<IUserServiceRead>();
        var teams = Substitute.For<ITeamManagementService>();
        var team = new Team { Id = Guid.NewGuid(), Name = "Alpha", Slug = "alpha", IsActive = true };
        void CheckCancellation(string read, CancellationToken ct)
        {
            if (string.Equals(boundary, read, StringComparison.Ordinal))
                ct.ThrowIfCancellationRequested();
        }
        users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(call =>
        {
            CheckCancellation("viewer", call.Arg<CancellationToken>());
            return new ValueTask<UserInfo?>(UserInfo.Create(new User { Id = userId }, [], [], [], null, []));
        });
        teams.GetTeamEntityBySlugAsync(team.Slug, Arg.Any<CancellationToken>()).Returns(call =>
        {
            CheckCancellation("entity", call.Arg<CancellationToken>());
            return Task.FromResult<Team?>(team);
        });
        teams.GetTeamAsync(team.Id, Arg.Any<CancellationToken>()).Returns(call =>
        {
            CheckCancellation("info", call.Arg<CancellationToken>());
            return Task.FromResult<TeamInfo?>(null);
        });
        teams.GetUserPendingRequestAsync(team.Id, userId, Arg.Any<CancellationToken>()).Returns(call =>
        {
            CheckCancellation("pending", call.Arg<CancellationToken>());
            return Task.FromResult<TeamJoinRequestSnapshot?>(null);
        });
        users.GetAllUserInfosAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<UserInfo>());
        teams.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, TeamInfo>());
        teams.GetMyTeamMembershipsAsync(userId, Arg.Any<CancellationToken>()).Returns(Array.Empty<MyTeamMembershipSummary>());
        var http = new DefaultHttpContext
        {
            RequestServices = services,
            RequestAborted = request.Token,
            Session = Substitute.For<ISession>(),
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"))
        };
        var controller = new TeamController(
            teams, Substitute.For<ITeamPageService>(), users,
            Substitute.For<ITeamResourceService>(), services.GetRequiredService<IStringLocalizer<TeamsResource>>(),
            services.GetRequiredService<IStringLocalizer<SharedResource>>(), new ConfigurationBuilder().Build(),
            new ConfigurationRegistry(), SystemClock.Instance, Substitute.For<IAuthorizationService>(),
            NullLogger<TeamController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>()),
            Url = Substitute.For<IUrlHelper>()
        };
        Task<IActionResult> ReadAsync() => action switch
        {
            "birthdays" => controller.Birthdays(1, request.Token),
            "my" => controller.MyTeams(request.Token),
            _ => controller.Join(team.Slug)
        };

        (await ReadAsync()).Should().BeOfType<ViewResult>();
        await request.CancelAsync();
        Func<Task> abandonedRead = async () => await ReadAsync();
        await abandonedRead.Should().ThrowAsync<OperationCanceledException>();
    }

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

    [HumansTheory]
    [Xunit.InlineData("en")]
    [Xunit.InlineData("es")]
    [Xunit.InlineData("de")]
    [Xunit.InlineData("it")]
    [Xunit.InlineData("fr")]
    [Xunit.InlineData("ca")]
    public async Task Join_InvalidMessageReturnsTheFormWithoutSubmittingARequest(string culture)
    {
        using var cultureScope = new CultureScope(culture);
        var registrations = new ServiceCollection().AddLogging().AddLocalization();
        registrations.AddControllers().AddDataAnnotationsLocalization(options =>
            options.DataAnnotationLocalizerProvider = (_, factory) => factory.Create(typeof(SharedResource)));
        using var services = registrations.BuildServiceProvider();
        var userId = Guid.NewGuid();
        var users = Substitute.For<IUserServiceRead>();
        users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(UserInfo.Create(new User { Id = userId }, [], [], [], null, []));
        var team = new Team { Id = Guid.NewGuid(), Name = "Alpha", Slug = "alpha", RequiresApproval = true };
        var teams = Substitute.For<ITeamManagementService>();
        teams.GetTeamEntityBySlugAsync(team.Slug, Arg.Any<CancellationToken>()).Returns(team);
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
            new ConfigurationRegistry(), SystemClock.Instance, Substitute.For<IAuthorizationService>(),
            NullLogger<TeamController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>()),
            Url = Substitute.For<IUrlHelper>()
        };
        var model = new JoinTeamViewModel
        {
            TeamId = team.Id,
            TeamName = "Spoofed",
            TeamSlug = "wrong",
            RequiresApproval = false,
            Message = new string('x', 2001)
        };
        services.GetRequiredService<IObjectModelValidator>().Validate(controller.ControllerContext, null, "", model);

        var result = await controller.Join(team.Slug, model);

        var returned = result.Should().BeOfType<ViewResult>().Subject.Model
            .Should().BeOfType<JoinTeamViewModel>().Subject;
        returned.Message.Should().Be(model.Message);
        returned.TeamName.Should().Be(team.Name);
        returned.TeamSlug.Should().Be(team.Slug);
        returned.RequiresApproval.Should().BeTrue();
        var expected = services.GetRequiredService<IStringLocalizer<SharedResource>>()
            ["Validation_MaxLength", "", 2000];
        expected.ResourceNotFound.Should().BeFalse();
        controller.ModelState[nameof(model.Message)]!.Errors.Should().ContainSingle()
            .Which.ErrorMessage.Should().Be(expected.Value);
        await teams.DidNotReceive().JoinTeamAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());

        controller.ModelState.Clear();
        model.Message = new string('x', 2000);
        services.GetRequiredService<IObjectModelValidator>().Validate(controller.ControllerContext, null, "", model);
        controller.ModelState.IsValid.Should().BeTrue();
        (await controller.Join(team.Slug, model)).Should().BeOfType<RedirectToActionResult>();
        await teams.Received(1).JoinTeamAsync(team.Id, userId, model.Message, Arg.Any<CancellationToken>());

        controller.ModelState.AddModelError(nameof(model.Message), "invalid");
        team.SystemTeamType = SystemTeamType.Volunteers;
        (await controller.Join(team.Slug, model)).Should().BeOfType<RedirectToActionResult>();
        controller.TempData[TempDataKeys.ErrorMessage].Should().Be(
            services.GetRequiredService<IStringLocalizer<TeamsResource>>()["Team_CannotJoinSystem"].Value);
        team.SystemTeamType = SystemTeamType.None;
        team.IsHidden = true;
        (await controller.Join(team.Slug, model)).Should().BeOfType<NotFoundResult>();
        model.TeamId = Guid.NewGuid();
        (await controller.Join(team.Slug, model)).Should().BeOfType<BadRequestResult>();
        await teams.Received(1).JoinTeamAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());

        team.IsHidden = false;
        team.IsActive = false;
        model.TeamId = team.Id;
        controller.ModelState.Clear();
        (await controller.Join(team.Slug)).Should().BeOfType<NotFoundResult>();
        (await controller.Join(team.Slug, model)).Should().BeOfType<NotFoundResult>();
        await teams.Received(1).JoinTeamAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
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
