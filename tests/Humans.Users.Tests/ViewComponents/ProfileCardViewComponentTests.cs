using Humans.Email.Contracts;
using Humans.Users.Tests.Infrastructure;
using AwesomeAssertions;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Humans.Auth.Contracts;
using Humans.Base.Enums;
using Humans.Governance.Contracts;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using Humans.Users.Models;
using Humans.Users.ViewComponents;
using Humans.Base.ViewComponents;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NSubstitute;

namespace Humans.Users.Tests.ViewComponents;

public sealed class ProfileCardViewComponentTests
{
    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task VolunteerHistoryPreview_PreservesWholeSurrogatePairs(bool fits)
    {
        var prefix = new string('a', fits ? 78 : 79);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(ProfileCardViewComponentTests).Assembly.GetName().Name,
            EnvironmentName = "Testing",
        });
        builder.Services.AddLocalization();
        builder.Services.AddControllersWithViews()
            .AddApplicationPart(typeof(UsersResource).Assembly)
            .AddApplicationPart(typeof(HumanViewComponent).Assembly)
            // <vc:nobodies-email-badge> is GoogleIntegration's.
            .AddApplicationPart(typeof(Humans.GoogleIntegration.ViewComponents.NobodiesEmailBadgeViewComponent).Assembly);
        builder.Services.AddSingleton(Substitute.For<IUserService>());
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(AuthorizationResult.Failed());
        builder.Services.AddSingleton(authorization);
        var url = Substitute.For<IUrlHelper>();
        url.Action(Arg.Any<UrlActionContext>()).Returns("/");
        var urls = Substitute.For<IUrlHelperFactory>();
        urls.GetUrlHelper(Arg.Any<ActionContext>()).Returns(url);
        builder.Services.AddSingleton(urls);
        await using var app = builder.Build();
        using var scope = app.Services.CreateScope();
        var services = scope.ServiceProvider;
        var result = services.GetRequiredService<IRazorViewEngine>()
            .GetView(null, "/Views/Shared/Components/ProfileCard/Default.cshtml", isMainPage: false);
        result.Success.Should().BeTrue();
        var http = new DefaultHttpContext { RequestServices = services };
        var route = new RouteData();
        route.Values["controller"] = "Profile";
        var action = new ActionContext(http, route, new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
        var data = new ViewDataDictionary(services.GetRequiredService<IModelMetadataProvider>(), new ModelStateDictionary())
        {
            Model = new ProfileCardViewModel
            {
                ViewMode = ProfileCardViewMode.Public,
                VolunteerHistory = [new VolunteerHistoryEntryViewModel
                {
                    EventName = "Example", Date = new LocalDate(2026, 7, 1), Description = prefix + "😀tail",
                }],
            },
        };
        using var writer = new StringWriter();
        var context = new ViewContext(action, result.View!, data,
            new TempDataDictionary(http, Substitute.For<ITempDataProvider>()), writer, new HtmlHelperOptions());

        await result.View!.RenderAsync(context);

        writer.ToString().Should().Contain(HtmlEncoder.Default.Encode(prefix + (fits ? "😀" : "") + "..."));
    }

    [HumansTheory]
    [Xunit.InlineData("Public")]
    [Xunit.InlineData("Self")]
    [Xunit.InlineData("Admin")]
    public async Task TeamNames_PublicCardHidesHiddenParents_WhileSelfAndAdminKeepThem(string mode)
    {
        var viewMode = Enum.Parse<ProfileCardViewMode>(mode);
        var userId = Guid.NewGuid();
        var now = Instant.FromUtc(2026, 1, 1, 0, 0);
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(new ValueTask<UserInfo?>(
            UserInfo.Create(new User { Id = userId, BurnerName = "Ada" }, [], [], [], null, [])));
        var parent = new TeamInfo(Guid.NewGuid(), "Hidden", null, "hidden", true, false,
            SystemTeamType.None, false, false, true, false, now,
            [new(Guid.NewGuid(), userId, "Ada", null, null, TeamMemberRole.Member, now)]);
        var child = parent with { Id = Guid.NewGuid(), Name = "Visible", Slug = "visible", IsHidden = false, ParentTeamId = parent.Id };
        var teams = Substitute.For<ITeamService>();
        teams.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, TeamInfo>
        {
            [parent.Id] = parent,
            [child.Id] = child
        });
        var membership = Substitute.For<IMembershipCalculator>();
        membership.GetMembershipSnapshotAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new MembershipSnapshot(MembershipStatus.Active, true, 0, 0, []));
        var component = new ProfileCardViewComponent(
            users, Substitute.For<IContactFieldService>(), Substitute.For<IUserEmailService>(), teams,
            Substitute.For<IRoleAssignmentService>(), membership, Substitute.For<ICommunicationPreferenceService>(), ConsentReviewStubs.Create())
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext
                {
                    HttpContext = new DefaultHttpContext(),
                    ViewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary())
                }
            }
        };

        var result = await component.InvokeAsync(userId, viewMode);

        var viewData = result.Should().BeOfType<ViewViewComponentResult>().Which.ViewData
            ?? throw new InvalidOperationException("Profile card returned no view data.");
        var model = viewData.Model.Should().BeOfType<ProfileCardViewModel>().Which;
        model.Teams.Select(team => team.TeamName).Should().Equal(viewMode == ProfileCardViewMode.Public
            ? new[] { "Visible" } : new[] { "Hidden", "Hidden - Visible" });
    }

    [HumansFact]
    public async Task InvokeAsync_RequestAborted_PropagatesCancellation()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(Arg.Any<Guid>(), aborted.Token)
            .Returns(_ => ValueTask.FromException<UserInfo?>(new OperationCanceledException(aborted.Token)));
        var component = new ProfileCardViewComponent(
            users, Substitute.For<IContactFieldService>(), Substitute.For<IUserEmailService>(),
            Substitute.For<ITeamService>(), Substitute.For<IRoleAssignmentService>(),
            Substitute.For<IMembershipCalculator>(), Substitute.For<ICommunicationPreferenceService>(), ConsentReviewStubs.Create())
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
