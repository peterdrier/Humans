using System.Reflection;
using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base.Authorization;
using Humans.Base.Constants;
using Humans.Permissions.Contracts;
using Humans.Tasks.Contracts;
using Humans.Tasks.Controllers;
using Humans.Tasks.Domain;
using Humans.Tasks.Models;
using Humans.Tasks.Services;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using NSubstitute;
using Xunit;

namespace Humans.Tasks.Tests;

/// <summary>The pages' deny paths and happy paths, over the real service and an in-memory repository.</summary>
public sealed class TasksControllerTests
{
    private readonly TaskTestKit _kit = new();
    private readonly Guid _me = Guid.NewGuid();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A signed-in human who manages the given teams and holds the given roles.</summary>
    private TasksController As(Guid[] manages, params string[] roles)
    {
        var permissions = Substitute.For<IPermissionService>();
        permissions.GetOwnersAsync(_me, TasksPermissions.Manage, Arg.Any<CancellationToken>())
            .Returns([.. manages.Select(id => new Owner(TeamOwner.Kind, id))]);

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, _me.ToString()), .. roles.Select(r => new Claim(ClaimTypes.Role, r))], "test");
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        return new TasksController(
            _kit.Service, permissions, _kit.Teams, _kit.Settings, Substitute.For<IUserService>(), new TaskTestKit.KeyLocalizer())
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>()),
        };
    }

    private TasksController Human() => As([]);

    private TasksController ManagerOfProduction() => As([TaskTestKit.Production]);

    private async Task<TaskInfo> TaskOnAsync(Guid team, bool hidden = false, Guid? confirmed = null)
    {
        var task = await _kit.CreateAsync(TaskTestKit.Input(team, hidden: hidden));
        if (confirmed is { } userId)
            (await _kit.Service.AssignAsync(task.Id, userId, _kit.Actor, Ct)).Succeeded.Should().BeTrue();
        return task;
    }

    private static TaskForm FormFor(Guid team, string title = "Paint the gate") => new() { TeamId = team, Title = title };

    [HumansFact]
    public async Task Index_ListsTasksButNotHiddenOnesToAPlainHuman()
    {
        var shown = await TaskOnAsync(TaskTestKit.Production);
        await TaskOnAsync(TaskTestKit.Production, hidden: true);
        await TaskOnAsync(TaskTestKit.HiddenTeam);

        var result = await Human().Index(new TaskListFilter(), Ct);

        var model = result.Should().BeOfType<ViewResult>().Which.Model.Should().BeOfType<TaskListViewModel>().Which;
        model.Tasks.Select(t => t.Id).Should().Equal(shown.Id);
    }

    [HumansFact]
    public async Task Index_ShowsEveryTaskToThoseWhoSeeAll()
    {
        await TaskOnAsync(TaskTestKit.Production);
        await TaskOnAsync(TaskTestKit.Production, hidden: true);
        await TaskOnAsync(TaskTestKit.HiddenTeam);

        var result = await As([], RoleNames.Board).Index(new TaskListFilter(), Ct);

        result.Should().BeOfType<ViewResult>().Which.Model.Should().BeOfType<TaskListViewModel>().Which.Tasks.Should().HaveCount(3);
    }

    [HumansFact]
    public async Task Index_LeavesOutFinishedTasksUnlessAskedToInclude()
    {
        var done = await TaskOnAsync(TaskTestKit.Production);
        await _kit.SetStatusAsync(done.Id, TaskItemStatus.Done);

        var byDefault = await Human().Index(new TaskListFilter(), Ct);
        var asked = await Human().Index(new TaskListFilter { Finished = true }, Ct);

        byDefault.Should().BeOfType<ViewResult>().Which.Model.Should().BeOfType<TaskListViewModel>().Which.Tasks.Should().BeEmpty();
        asked.Should().BeOfType<ViewResult>().Which.Model.Should().BeOfType<TaskListViewModel>().Which.Tasks.Should().ContainSingle();
    }

    [HumansFact]
    public async Task Details_AHiddenTaskIsNotFoundForAPlainHumanButShowsToItsPeople()
    {
        var task = await TaskOnAsync(TaskTestKit.Production, hidden: true, confirmed: _me);
        var other = await TaskOnAsync(TaskTestKit.Production, hidden: true);

        (await Human().Details(other.Id, Ct)).Should().BeOfType<NotFoundResult>();
        (await Human().Details(task.Id, Ct)).Should().BeOfType<ViewResult>();
        (await As([], RoleNames.Board).Details(other.Id, Ct)).Should().BeOfType<ViewResult>();
        (await Human().Details(Guid.NewGuid(), Ct)).Should().BeOfType<NotFoundResult>();
    }

    [HumansFact]
    public async Task Details_OffersManagementOnlyToManagers()
    {
        var task = await TaskOnAsync(TaskTestKit.Production);

        var asHuman = (await Human().Details(task.Id, Ct)).Should().BeOfType<ViewResult>().Which.Model.Should().BeOfType<TaskPageViewModel>().Which;
        var asManager = (await ManagerOfProduction().Details(task.Id, Ct)).Should().BeOfType<ViewResult>().Which.Model.Should().BeOfType<TaskPageViewModel>().Which;
        var asSeeAll = (await As([], RoleNames.Board).Details(task.Id, Ct)).Should().BeOfType<ViewResult>().Which.Model.Should().BeOfType<TaskPageViewModel>().Which;

        asHuman.CanManage.Should().BeFalse();
        asHuman.StatusChoices.Should().BeEmpty();
        asManager.CanManage.Should().BeTrue();
        asManager.StatusChoices.Should().Contain(TaskItemStatus.Cancelled);
        asSeeAll.CanManage.Should().BeFalse();
    }

    [HumansFact]
    public async Task Details_ShowsALinkToAHiddenTaskAsHidden()
    {
        var waiting = await TaskOnAsync(TaskTestKit.Production);
        var secret = await TaskOnAsync(TaskTestKit.Art, hidden: true);
        (await _kit.Service.AddDependencyAsync(waiting.Id, secret.Id, _kit.Actor, Ct)).Succeeded.Should().BeTrue();

        var model = (await Human().Details(waiting.Id, Ct)).Should().BeOfType<ViewResult>().Which.Model.Should().BeOfType<TaskPageViewModel>().Which;

        model.WaitingOn.Should().ContainSingle().Which.Title.Should().BeNull();
    }

    [HumansTheory]
    [InlineData("production")]
    [InlineData("PRODUCTION")]
    public async Task Team_TakesTheSlugOrTheTeamId(string slug)
    {
        await TaskOnAsync(TaskTestKit.Production);

        (await Human().Team(slug, new TaskListFilter(), Ct)).Should().BeOfType<ViewResult>();
        (await Human().Team(TaskTestKit.Production.ToString(), new TaskListFilter(), Ct)).Should().BeOfType<ViewResult>();
        (await Human().Team("nope", new TaskListFilter(), Ct)).Should().BeOfType<NotFoundResult>();
    }

    [HumansFact]
    public async Task Team_AHiddenTeamIsNotFoundForThoseWhoCannotSeeIt()
    {
        await TaskOnAsync(TaskTestKit.HiddenTeam);

        (await Human().Team("secret", new TaskListFilter(), Ct)).Should().BeOfType<NotFoundResult>();
        (await As([], RoleNames.Admin).Team("secret", new TaskListFilter(), Ct)).Should().BeOfType<ViewResult>();
        (await As([TaskTestKit.HiddenTeam]).Team("secret", new TaskListFilter(), Ct)).Should().BeOfType<ViewResult>();
    }

    [HumansFact]
    public async Task Team_OffersTheAddFormOnlyToManagersOfAnActiveTeam()
    {
        static bool CanAdd(IActionResult r) =>
            r.Should().BeOfType<ViewResult>().Which.Model.Should().BeOfType<TeamPageViewModel>().Which.CanAdd;

        CanAdd(await Human().Team("production", new TaskListFilter(), Ct)).Should().BeFalse();
        CanAdd(await As([], RoleNames.Board).Team("production", new TaskListFilter(), Ct)).Should().BeFalse();
        CanAdd(await ManagerOfProduction().Team("production", new TaskListFilter(), Ct)).Should().BeTrue();
        CanAdd(await As([TaskTestKit.Retired]).Team("retired", new TaskListFilter(), Ct)).Should().BeFalse();
    }

    [HumansFact]
    public async Task Add_NeedsManageOnThatTeam()
    {
        (await Human().Add("production", FormFor(TaskTestKit.Production), Ct)).Should().BeOfType<ForbidResult>();
        (await As([], RoleNames.Admin).Add("production", FormFor(TaskTestKit.Production), Ct)).Should().BeOfType<ForbidResult>();
        (await As([TaskTestKit.Art]).Add("production", FormFor(TaskTestKit.Production), Ct)).Should().BeOfType<ForbidResult>();
        (await _kit.Service.GetTasksAsync(Ct)).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Add_AManagerCannotSmuggleTheTaskOntoAnotherTeam()
    {
        var result = await ManagerOfProduction().Add("production", FormFor(TaskTestKit.Art), Ct);

        result.Should().BeOfType<ForbidResult>();
        (await _kit.Service.GetTasksAsync(Ct)).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Add_AManagerCreatesTheTaskAndLandsOnIt()
    {
        var form = FormFor(TaskTestKit.Production);
        form.NeedsApproval = true;
        form.ApprovalReason = "chainsaw licence";

        var result = await ManagerOfProduction().Add("production", form, Ct);

        var created = (await _kit.Service.GetTasksAsync(Ct)).Should().ContainSingle().Which;
        result.Should().BeOfType<RedirectToActionResult>().Which.RouteValues!["id"].Should().Be(created.Id);
        created.NeedsApproval.Should().BeTrue();
        created.ApprovalReason.Should().Be("chainsaw licence");
    }

    [HumansFact]
    public async Task Add_ARefusalShowsTheFormAgainWithWhatWasTyped()
    {
        var form = FormFor(TaskTestKit.Production, title: " ");

        var result = await ManagerOfProduction().Add("production", form, Ct);

        result.Should().BeOfType<ViewResult>().Which.Model.Should().BeOfType<TeamPageViewModel>().Which.AddForm.Should().BeSameAs(form);
        (await _kit.Service.GetTasksAsync(Ct)).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Edit_NeedsManageOnTheTasksTeam_AndOnBothTeamsToMove()
    {
        var task = await TaskOnAsync(TaskTestKit.Production);

        (await Human().Edit(task.Id, FormFor(TaskTestKit.Production, "Renamed"), Ct)).Should().BeOfType<ForbidResult>();
        (await As([TaskTestKit.Art]).Edit(task.Id, FormFor(TaskTestKit.Production, "Renamed"), Ct)).Should().BeOfType<ForbidResult>();
        (await ManagerOfProduction().Edit(task.Id, FormFor(TaskTestKit.Art, "Renamed"), Ct)).Should().BeOfType<ForbidResult>();
        (await _kit.Service.GetTaskAsync(task.Id, Ct))!.Title.Should().Be("Build the bar");
        (await _kit.Service.GetTaskAsync(task.Id, Ct))!.Owner.Id.Should().Be(TaskTestKit.Production);
    }

    [HumansFact]
    public async Task Edit_AManagerOfBothTeamsMovesTheTask()
    {
        var task = await TaskOnAsync(TaskTestKit.Production);

        var result = await As([TaskTestKit.Production, TaskTestKit.Art]).Edit(task.Id, FormFor(TaskTestKit.Art, "Renamed"), Ct);

        result.Should().BeOfType<RedirectToActionResult>();
        var saved = (await _kit.Service.GetTaskAsync(task.Id, Ct))!;
        saved.Title.Should().Be("Renamed");
        saved.Owner.Id.Should().Be(TaskTestKit.Art);
    }

    [HumansFact]
    public async Task Edit_AHiddenTaskIsNotFoundForSomeoneWhoCannotSeeIt()
    {
        var task = await TaskOnAsync(TaskTestKit.Production, hidden: true);

        (await Human().Edit(task.Id, FormFor(TaskTestKit.Production), Ct)).Should().BeOfType<NotFoundResult>();
    }

    [HumansFact]
    public async Task SetStatus_APlainHumanAndABoardMemberCannot()
    {
        var task = await TaskOnAsync(TaskTestKit.Production);
        var form = new StatusForm { Status = TaskItemStatus.Done };

        (await Human().SetStatus(task.Id, form, Ct)).Should().BeOfType<ForbidResult>();
        (await As([], RoleNames.Board).SetStatus(task.Id, form, Ct)).Should().BeOfType<ForbidResult>();
        (await _kit.Service.GetTaskAsync(task.Id, Ct))!.Status.Should().Be(TaskItemStatus.Todo);
    }

    [HumansFact]
    public async Task SetStatus_APersonOnTheTaskMayFinishButNeverCancel()
    {
        var task = await TaskOnAsync(TaskTestKit.Production, confirmed: _me);

        (await Human().SetStatus(task.Id, new StatusForm { Status = TaskItemStatus.Cancelled }, Ct)).Should().BeOfType<ForbidResult>();
        (await Human().SetStatus(task.Id, new StatusForm { Status = TaskItemStatus.Done }, Ct)).Should().BeOfType<RedirectToActionResult>();
        (await _kit.Service.GetTaskAsync(task.Id, Ct))!.Status.Should().Be(TaskItemStatus.Done);
    }

    [HumansFact]
    public async Task SetStatus_AManagerMayCancel()
    {
        var task = await TaskOnAsync(TaskTestKit.Production);

        await ManagerOfProduction().SetStatus(task.Id, new StatusForm { Status = TaskItemStatus.Cancelled }, Ct);

        (await _kit.Service.GetTaskAsync(task.Id, Ct))!.Status.Should().Be(TaskItemStatus.Cancelled);
    }

    [HumansFact]
    public async Task Delete_NeedsManage_AndAPersonOnTheTaskCannotDeleteIt()
    {
        var task = await TaskOnAsync(TaskTestKit.Production, confirmed: _me);

        (await Human().Delete(task.Id, Ct)).Should().BeOfType<ForbidResult>();
        (await As([], RoleNames.Board).Delete(task.Id, Ct)).Should().BeOfType<ForbidResult>();
        (await _kit.Service.GetTaskAsync(task.Id, Ct)).Should().NotBeNull();

        (await ManagerOfProduction().Delete(task.Id, Ct)).Should().BeOfType<RedirectToActionResult>();
        (await _kit.Service.GetTaskAsync(task.Id, Ct)).Should().BeNull();
    }

    [HumansFact]
    public async Task Assign_NeedsManage_AndAnUnknownPersonIsRefused()
    {
        var task = await TaskOnAsync(TaskTestKit.Production);
        var someone = Guid.NewGuid();

        (await Human().Assign(task.Id, someone, Ct)).Should().BeOfType<ForbidResult>();
        (await As([TaskTestKit.Art]).Assign(task.Id, someone, Ct)).Should().BeOfType<ForbidResult>();

        var refused = ManagerOfProduction();
        (await refused.Assign(task.Id, someone, Ct)).Should().BeOfType<RedirectToActionResult>();
        refused.TempData.ContainsKey(TempDataKeys.ErrorMessage).Should().BeTrue();
        (await _kit.Service.GetTaskAsync(task.Id, Ct))!.Assignments.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Unassign_NeedsManage()
    {
        var task = await TaskOnAsync(TaskTestKit.Production, confirmed: _me);

        (await Human().Unassign(task.Id, _me, Ct)).Should().BeOfType<ForbidResult>();
        (await _kit.Service.GetTaskAsync(task.Id, Ct))!.Assignments.Should().ContainSingle();

        (await ManagerOfProduction().Unassign(task.Id, _me, Ct)).Should().BeOfType<RedirectToActionResult>();
        (await _kit.Service.GetTaskAsync(task.Id, Ct))!.Assignments.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Dependencies_NeedManageOnTheWaitingTask_AndCannotPointAtATaskTheManagerCannotSee()
    {
        var waiting = await TaskOnAsync(TaskTestKit.Production);
        var other = await TaskOnAsync(TaskTestKit.Art);
        var secret = await TaskOnAsync(TaskTestKit.Art, hidden: true);

        (await Human().AddDependency(waiting.Id, other.Id, Ct)).Should().BeOfType<ForbidResult>();
        (await As([TaskTestKit.Art]).AddDependency(waiting.Id, other.Id, Ct)).Should().BeOfType<ForbidResult>();

        await ManagerOfProduction().AddDependency(waiting.Id, secret.Id, Ct);
        (await _kit.Service.GetTaskAsync(waiting.Id, Ct))!.WaitingOn.Should().BeEmpty();

        await ManagerOfProduction().AddDependency(waiting.Id, other.Id, Ct);
        (await _kit.Service.GetTaskAsync(waiting.Id, Ct))!.WaitingOn.Should().ContainSingle();

        (await Human().RemoveDependency(waiting.Id, other.Id, Ct)).Should().BeOfType<ForbidResult>();
        (await _kit.Service.GetTaskAsync(waiting.Id, Ct))!.WaitingOn.Should().ContainSingle();

        await ManagerOfProduction().RemoveDependency(waiting.Id, other.Id, Ct);
        (await _kit.Service.GetTaskAsync(waiting.Id, Ct))!.WaitingOn.Should().BeEmpty();
    }

    [HumansFact]
    public void EveryPostChecksTheAntiForgeryToken() =>
        typeof(TasksController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttribute<HttpPostAttribute>() is not null)
            .Should().OnlyContain(m => m.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>() != null);

    [HumansFact]
    public void TheAdminPageIsForTasksReaders()
    {
        typeof(TasksAdminController).GetCustomAttributes<AuthorizeAttribute>(inherit: false)
            .Should().ContainSingle(a => a.Policy == TasksPolicies.Read);
        typeof(TasksAdminController).GetCustomAttribute<RouteAttribute>()!.Template.Should().Be("Tasks/Admin");
    }
}
