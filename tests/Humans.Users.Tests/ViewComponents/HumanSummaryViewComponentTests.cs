using System.Globalization;
using System.Resources;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Humans.Base;
using Humans.Base.Extensions;
using Humans.Base.ViewComponents;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Humans.Users.Tests.Infrastructure;
using Humans.Governance.Contracts;
using AwesomeAssertions;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using Humans.Users.ViewComponents;
using Humans.Users.Models;
using Humans.Base.Enums;
using NodaTime;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using NSubstitute;

namespace Humans.Users.Tests.ViewComponents;

public sealed class HumanSummaryViewComponentTests
{
    [HumansFact]
    public async Task InvokeAsync_ProfiledHuman_UsesResolvedIdentityAndSummaryProjection()
    {
        var userId = Guid.NewGuid();
        var requestedId = Guid.NewGuid();
        var at = Instant.FromUtc(2026, 10, 22, 0, 0);
        var profile = new Profile
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            City = "Madrid",
            CountryCode = "ES",
            ProfilePictureContentType = "image/jpeg",
            UpdatedAt = at,
        };
        var info = new User
        {
            Id = userId,
            BurnerName = "Sparkle",
            State = UserState.Suspended,
            PreferredLanguage = "es",
        }.ToUserInfo(profile: profile);
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(requestedId, Arg.Any<CancellationToken>()).Returns(info);
        var teams = Substitute.For<ITeamService>();
        TeamInfo Team(string name, bool hidden = false, bool active = true, SystemTeamType system = SystemTeamType.None) =>
            new(Guid.NewGuid(), name, null, name, active, system != SystemTeamType.None, system,
                false, false, hidden, false, at,
                [new TeamMemberInfo(Guid.NewGuid(), userId, "Sparkle", null, null, TeamMemberRole.Member, at)]);
        teams.GetUserTeamMembershipsAsync(userId, Arg.Any<CancellationToken>()).Returns(
            new[] { Team("Zulu"), Team("Hidden", hidden: true), Team("Alpha"),
                Team("Inactive", active: false), Team("Volunteers", system: SystemTeamType.Volunteers) }
                .Select(t => new UserTeamMembershipInfo(t.Members[0].TeamMemberId, t.Id, t.Name, t.Slug,
                    t.IsSystemTeam, t.Members[0].Role, at)
                {
                    IsActive = t.IsActive,
                    IsHidden = t.IsHidden,
                    SystemTeamType = t.SystemTeamType
                }).ToList());
        var governance = Substitute.For<IGovernanceService>();
        governance.GetMembershipTierAsync(userId, Arg.Any<CancellationToken>()).Returns(MembershipTier.Asociado);
        var component = new HumanSummaryViewComponent(users, teams, governance)
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

        var result = await component.InvokeAsync(requestedId);

        var model = result.Should().BeOfType<ViewViewComponentResult>().Subject.ViewData?.Model;
        var vm = model.Should().BeOfType<ProfileSummaryViewModel>().Subject;
        vm.UserId.Should().Be(userId);
        vm.MembershipTier.Should().Be("Asociado");
        vm.MembershipStatus.Should().Be("Suspended");
        vm.IsSuspended.Should().BeTrue();
        vm.City.Should().Be("Madrid");
        vm.Teams.Should().Equal("Alpha", "Zulu");
        vm.HiddenTeams.Should().Equal("Hidden");
    }

    [HumansFact]
    public async Task InvokeAsync_ProfilelessHuman_UsesCanonicalEmailFallback()
    {
        var userId = Guid.NewGuid();
        var info = new User { Id = userId }.ToUserInfo() with { IdentityEmailColumn = "canonical@example.com" };
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(info);
        var component = new HumanSummaryViewComponent(users, Substitute.For<ITeamService>(), Substitute.For<IGovernanceService>())
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

        var result = await component.InvokeAsync(userId);

        result.Should().BeOfType<ViewViewComponentResult>().Which.ViewData!.Model
            .Should().BeOfType<ProfileSummaryViewModel>().Which.Email.Should().Be("canonical@example.com");
    }

    [HumansFact]
    public async Task InvokeAsync_RequestAborted_PropagatesCancellation()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(Arg.Any<Guid>(), aborted.Token)
            .Returns(_ => ValueTask.FromException<UserInfo?>(new OperationCanceledException(aborted.Token)));
        var component = new HumanSummaryViewComponent(users, Substitute.For<ITeamService>(), Substitute.For<IGovernanceService>())
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext
                {
                    HttpContext = new DefaultHttpContext { RequestAborted = aborted.Token }
                }
            }
        };

        var act = () => component.InvokeAsync(Guid.NewGuid());

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
    [HumansFact]
    public async Task Popovers_resolve_names_and_localize_missing_identity_without_exposing_admin_fields()
    {
        using var culture = new CultureScope("en");
        var id = Guid.NewGuid();
        const string name = "Current <script>&";
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(id, Arg.Any<CancellationToken>())
            .Returns(UserInfo.Create(new User { Id = id, BurnerName = name }, [], [], null));
        var publicModel = new PublicPopoverViewModel { UserId = id, RoleLabels = ["Coordinator · Team"] };
        var fullModel = new ProfileSummaryViewModel { UserId = id, HiddenTeams = ["Private team"], CampName = "Private camp" };
        var full = await RenderAsync("/Views/Shared/_HumanPopover.cshtml", fullModel, users);
        var reduced = await RenderAsync("/Views/Shared/_HumanPopoverPublic.cshtml", publicModel, users);
        foreach (var html in new[] { full, reduced })
            html.Should().Contain(HtmlEncoder.Default.Encode(name)).And.NotContain("<script>")
                .And.NotContain("Private team").And.NotContain("Private camp");
        reduced.Should().Contain("Coordinator").And.NotContain("fa-location-dot");
        var missing = await RenderAsync("/Views/Shared/_HumanPopoverPublic.cshtml", new PublicPopoverViewModel { UserId = Guid.NewGuid() }, users);
        var unknown = new ResourceManager(typeof(SharedResource)).GetString("Common_Unknown", CultureInfo.CurrentUICulture)!;
        missing.Should().Contain(HtmlEncoder.Default.Encode(unknown));
    }

    [HumansFact]
    public async Task Merge_history_and_choices_render_exact_account_names_without_following_redirects()
    {
        var id = Guid.NewGuid();
        var users = Substitute.For<IUserService>();
        const string name = "Archived <script>&";
        users.GetRawUserInfoAsync(id, Arg.Any<CancellationToken>())
            .Returns(UserInfo.Create(new User { Id = id, BurnerName = name }, [], [], null));
        users.GetUserInfoAsync(id, Arg.Any<CancellationToken>())
            .Returns(UserInfo.Create(new User { Id = Guid.NewGuid(), BurnerName = "Survivor name" }, [], [], null));
        var history = await RenderAsync("/Views/Shared/Components/MergedAccounts/Default.cshtml",
            new MergedAccountsViewModel { MergedIn = [new MergedAccountViewModel { UserId = id }] }, users);
        var choices = await RenderAsync("/Views/UsersAdminAccountMerges/Index.cshtml",
            new AccountMergeQueueViewModel { Rows = [new AccountMergeRowViewModel { AccountA = new ProfileSummaryViewModel { UserId = id } }] }, users);
        history.Should().Contain(HtmlEncoder.Default.Encode(name)).And.NotContain("Survivor name")
            .And.NotContain("data-human-popover");
        choices.Should().Contain($"value=\"{id}\"").And.Contain(HtmlEncoder.Default.Encode(name));
        // Profile cards resolve normally; only the exact-account choice label uses the raw name.
        choices.Should().Contain("Survivor name");
    }

    private static async Task<string> RenderAsync(string path, object model, IUserService users)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(HumanSummaryViewComponentTests).Assembly.GetName().Name,
            EnvironmentName = "Testing",
        });
        builder.Services.AddLocalization();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddControllersWithViews()
            .AddApplicationPart(typeof(UsersResource).Assembly)
            .AddApplicationPart(typeof(HumanViewComponent).Assembly)
            // <vc:nobodies-email-badge> is GoogleIntegration's.
            .AddApplicationPart(typeof(Humans.GoogleIntegration.ViewComponents.NobodiesEmailBadgeViewComponent).Assembly)
            .AddViewLocalization();
        builder.Services.AddSingleton(users);
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
            .GetView(null, path, isMainPage: false);
        result.Success.Should().BeTrue();
        var http = new DefaultHttpContext { RequestServices = services };
        var route = new RouteData();
        route.Values["controller"] = "Profile";
        var action = new ActionContext(http, route, new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
        var data = new ViewDataDictionary(services.GetRequiredService<IModelMetadataProvider>(), new ModelStateDictionary())
        {
            Model = model,
        };
        using var writer = new StringWriter();
        var context = new ViewContext(action, result.View!, data,
            new TempDataDictionary(http, Substitute.For<ITempDataProvider>()), writer, new HtmlHelperOptions());
        await result.View!.RenderAsync(context);
        return writer.ToString();
    }

}
