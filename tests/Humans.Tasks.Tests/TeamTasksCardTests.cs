using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base.Authorization;
using Humans.Base.Constants;
using Humans.Base.Interfaces;
using Humans.Permissions.Contracts;
using Humans.Tasks.Contracts;
using Humans.Tasks.Domain;
using Humans.Tasks.Models;
using Humans.Tasks.Services;
using Humans.Tasks.ViewComponents;
using Humans.Teams.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Humans.Tasks.Tests;

/// <summary>The tasks card on a team's page: what each kind of viewer sees of a team's tasks.</summary>
public sealed class TeamTasksCardTests
{
    private readonly TaskTestKit _kit = new();
    private readonly Guid _me = Guid.NewGuid();
    private string _userState = "Active";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly IAuthorizationService Authorization = new ServiceCollection()
        .AddLogging()
        .AddAuthorizationCore(o => ((ISectionPolicies)new Section()).AddPolicies(o))
        .BuildServiceProvider()
        .GetRequiredService<IAuthorizationService>();

    private async Task<IViewComponentResult> CardAsync(Guid teamId, Guid[]? manages = null, params string[] roles)
    {
        var permissions = Substitute.For<IPermissionService>();
        permissions.GetOwnersAsync(_me, TasksPermissions.Manage, Arg.Any<CancellationToken>())
            .Returns([.. (manages ?? []).Select(id => new Owner(TeamOwner.Kind, id))]);

        var url = Substitute.For<IUrlHelper>();
        url.Action(Arg.Any<UrlActionContext>()).Returns(call => $"/Tasks/Team/{new RouteValueDictionary(call.Arg<UrlActionContext>().Values)["slug"]}");
        var factory = Substitute.For<IUrlHelperFactory>();
        factory.GetUrlHelper(Arg.Any<ActionContext>()).Returns(url);

        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, _me.ToString()), new Claim(RoleChecks.UserStateClaimType, _userState), .. roles.Select(r => new Claim(ClaimTypes.Role, r))], "test")),
            RequestServices = new ServiceCollection().AddSingleton(factory).AddSingleton(Substitute.For<ICompositeViewEngine>()).BuildServiceProvider(),
        };
        http.Request.Path = "/Teams/production";
        http.Request.QueryString = new QueryString("?x=1");

        var viewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary());
        var card = new TeamTasksCardViewComponent(_kit.Service, _kit.Teams, permissions, Authorization)
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext { HttpContext = http, ViewData = viewData },
            },
        };
        return await card.InvokeAsync(teamId);
    }

    private static TeamTasksCardViewModel Model(IViewComponentResult result) =>
        result.Should().BeOfType<ViewViewComponentResult>().Which.ViewData!.Model.Should().BeOfType<TeamTasksCardViewModel>().Which;

    private Task<TaskInfo> TaskAsync(Guid team, string title = "Build the bar", bool hidden = false, SlotPriority priority = SlotPriority.Important) =>
        _kit.CreateAsync(TaskTestKit.Input(team, title, hidden: hidden) with { Priority = priority });

    [HumansFact]
    public async Task Card_OpenTasks_ListMostNeededFirst()
    {
        var nice = await TaskAsync(TaskTestKit.Production, "Stools", priority: SlotPriority.NiceToHave);
        var critical = await TaskAsync(TaskTestKit.Production, "Bar", priority: SlotPriority.Critical);
        var done = await TaskAsync(TaskTestKit.Production, "Old");
        await _kit.SetStatusAsync(done.Id, TaskItemStatus.Done);
        await TaskAsync(TaskTestKit.Art, "Mural");

        var model = Model(await CardAsync(TaskTestKit.Production));

        model.Rows.Tasks.Select(t => t.Id).Should().Equal(critical.Id, nice.Id);
        model.Total.Should().Be(2);
        model.Rows.Places.Should().ContainKey(critical.Id).WhoseValue.Should().Be(MyPlace.CanClaim);
        model.Rows.ReturnUrl.Should().Be("/Teams/production?x=1", "a claim made on the card comes back to the team page");
        model.TasksUrl.Should().EndWith("production");
    }

    [HumansFact]
    public async Task Card_SuspendedMember_OffersNoClaim()
    {
        var task = await TaskAsync(TaskTestKit.Production);
        _userState = "Suspended";

        Model(await CardAsync(TaskTestKit.Production)).Rows.Places.Should().NotContainKey(task.Id);
    }

    [HumansFact]
    public async Task Card_ManyTasks_ShowOnlyTheFirstFew()
    {
        for (var i = 0; i < TeamTasksCardViewComponent.RowLimit + 2; i++)
            await TaskAsync(TaskTestKit.Production, $"Task {i}");

        var model = Model(await CardAsync(TaskTestKit.Production));

        model.Rows.Tasks.Should().HaveCount(TeamTasksCardViewComponent.RowLimit);
        model.Total.Should().Be(TeamTasksCardViewComponent.RowLimit + 2);
    }

    [HumansFact]
    public async Task Card_HiddenTask_ShowsOnlyToThoseWhoSeeIt()
    {
        var shown = await TaskAsync(TaskTestKit.Production, "Shown");
        var hidden = await TaskAsync(TaskTestKit.Production, "Hidden", hidden: true);

        Model(await CardAsync(TaskTestKit.Production)).Rows.Tasks.Select(t => t.Id).Should().Equal(shown.Id);
        Model(await CardAsync(TaskTestKit.Production, [TaskTestKit.Production])).Rows.Tasks.Select(t => t.Id).Should().Contain(hidden.Id);
        Model(await CardAsync(TaskTestKit.Production, null, RoleNames.Board)).Rows.Tasks.Select(t => t.Id).Should().Contain(hidden.Id);
    }

    [HumansFact]
    public async Task Card_HiddenTeam_ShowsNothingToOutsiders()
    {
        await TaskAsync(TaskTestKit.HiddenTeam);

        (await CardAsync(TaskTestKit.HiddenTeam)).Should().BeOfType<ContentViewComponentResult>();
        Model(await CardAsync(TaskTestKit.HiddenTeam, [TaskTestKit.HiddenTeam])).Total.Should().Be(1);
    }

    [HumansFact]
    public async Task Card_WaitingRequests_ShowOnlyToManagers()
    {
        var task = await _kit.CreateAsync(TaskTestKit.Input(needsApproval: true));
        (await _kit.Service.ClaimAsync(task.Id, Guid.NewGuid(), Ct)).Succeeded.Should().BeTrue();
        (await _kit.Service.ClaimAsync(task.Id, Guid.NewGuid(), Ct)).Succeeded.Should().BeTrue();

        var manager = Model(await CardAsync(TaskTestKit.Production, [TaskTestKit.Production]));
        manager.WaitingRequests.Should().Be(2);
        manager.CanAdd.Should().BeTrue();
        manager.RequestsUrl.Should().EndWith("#requests");

        var plain = Model(await CardAsync(TaskTestKit.Production));
        plain.WaitingRequests.Should().Be(0);
        plain.CanAdd.Should().BeFalse();
        Model(await CardAsync(TaskTestKit.Production, [TaskTestKit.Art])).WaitingRequests.Should().Be(0);
    }

    [HumansFact]
    public async Task Card_NoTasks_ShowsAnEmptyCardToManagersOnly()
    {
        var manager = Model(await CardAsync(TaskTestKit.Production, [TaskTestKit.Production]));
        manager.Rows.Tasks.Should().BeEmpty();
        manager.Total.Should().Be(0);
        manager.CanAdd.Should().BeTrue();

        (await CardAsync(TaskTestKit.Production)).Should().BeOfType<ContentViewComponentResult>("a member with nothing to see and nothing to add gets no card");
        (await CardAsync(TaskTestKit.Production, null, RoleNames.Board)).Should().BeOfType<ContentViewComponentResult>();
    }

    [HumansFact]
    public async Task Card_OnlyHiddenTasks_ShowsNothingToOutsiders()
    {
        await TaskAsync(TaskTestKit.Production, hidden: true);

        (await CardAsync(TaskTestKit.Production)).Should().BeOfType<ContentViewComponentResult>();
        Model(await CardAsync(TaskTestKit.Production, null, RoleNames.Board)).Total.Should().Be(1);
    }

    [HumansFact]
    public async Task Card_TeamThatCannotHoldTasks_ShowsNothing()
    {
        (await CardAsync(TaskTestKit.Everyone)).Should().BeOfType<ContentViewComponentResult>();
        (await CardAsync(TaskTestKit.Retired)).Should().BeOfType<ContentViewComponentResult>();
        (await CardAsync(Guid.NewGuid())).Should().BeOfType<ContentViewComponentResult>();
    }
}
