using System.Globalization;
using System.Reflection;
using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base.Authorization;
using Humans.Base.Constants;
using Humans.Base.Interfaces;
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
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using NSubstitute;
using Xunit;

namespace Humans.Tasks.Tests;

/// <summary>The pages' deny paths and happy paths, over the real service and an in-memory repository.</summary>
public sealed class TasksControllerTests
{
    private readonly TaskTestKit _kit = new();
    private readonly Guid _me = Guid.NewGuid();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The section's own registered policies, evaluated for real.</summary>
    private static readonly IAuthorizationService Authorization = new ServiceCollection()
        .AddLogging()
        .AddAuthorizationCore(o => ((ISectionPolicies)new Section()).AddPolicies(o))
        .BuildServiceProvider()
        .GetRequiredService<IAuthorizationService>();

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
            _kit.Service, permissions, _kit.Teams, _kit.Settings, Substitute.For<IUserService>(), Authorization, new TaskTestKit.KeyLocalizer(), NullLogger<TasksController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>()),
            Url = LocalUrls(),
        };
    }

    /// <summary>A url helper that tells local paths from off-site ones, as the real one does.</summary>
    private static IUrlHelper LocalUrls()
    {
        var url = Substitute.For<IUrlHelper>();
        url.IsLocalUrl(Arg.Any<string?>()).Returns(call =>
            call.Arg<string?>() is { Length: > 0 } u && u[0] == '/' && (u.Length == 1 || (u[1] != '/' && u[1] != '\\')));
        return url;
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

    public static IEnumerable<object?[]> GetRequiredFieldCases()
    {
        foreach (var editing in new[] { false, true })
        {
            foreach (var field in new[] { "Priority", "SlotCount", "StartRelative", "DueRelative", "NeedsApproval", "IsHidden" })
            {
                yield return [editing, field, null];
                yield return [editing, field, "bad"];
            }
            yield return [editing, null, null];
        }
    }

    [HumansTheory]
    [MemberData(nameof(GetRequiredFieldCases))]
    public async Task TaskForms_IncompleteFields_RefuseWrites(bool editing, string? field, string? value)
    {
        var existing = editing ? await _kit.CreateAsync(TaskTestKit.Input(slots: 3, needsApproval: true,
            reason: "Training", hidden: true)) : null;
        var controller = ManagerOfProduction();
        var fields = new Dictionary<string, StringValues>(StringComparer.Ordinal)
        {
            ["TeamId"] = TaskTestKit.Production.ToString(),
            ["Title"] = "Paint the gate",
            ["EventId"] = TaskTestKit.Spring.ToString(),
            ["StartDate"] = "2027-03-10",
            ["DueDate"] = "2027-03-11",
            ["StartRelative"] = "false",
            ["DueRelative"] = "false",
            ["Priority"] = "None",
            ["SlotCount"] = "4",
            ["NeedsApproval"] = "false",
            ["IsHidden"] = "false",
        };
        if (field is not null)
        {
            if (value is null) fields.Remove(field);
            else fields[field] = value;
        }
        var action = editing ? nameof(TasksController.Edit) : nameof(TasksController.Add);
        var form = await BindAsync<TaskForm>(controller, action, fields);
        _kit.Audit.ClearReceivedCalls();

        var result = editing
            ? await controller.Edit(existing!.Id, form, Ct)
            : await controller.Add("production", form, Ct);

        if (field is not null)
        {
            result.Should().BeOfType<ViewResult>();
            _kit.Audit.ReceivedCalls().Should().BeEmpty();
            var saved = await _kit.Service.GetTasksAsync(Ct);
            if (editing) saved.Should().ContainSingle().Which.Should().BeEquivalentTo(existing);
            else saved.Should().BeEmpty();
        }
        else
        {
            result.Should().BeOfType<RedirectToActionResult>();
            var saved = (await _kit.Service.GetTasksAsync(Ct)).Should().ContainSingle().Which;
            saved.Priority.Should().Be(SlotPriority.None);
            saved.SlotCount.Should().Be(4);
            saved.NeedsApproval.Should().BeFalse();
            saved.IsHidden.Should().BeFalse();
            saved.Start!.IsRelative.Should().BeFalse();
            saved.Due!.IsRelative.Should().BeFalse();
        }
    }

    [HumansTheory]
    [InlineData(null)]
    [InlineData("bad")]
    [InlineData("Todo")]
    public async Task SetStatus_IncompleteField_PreservesBlockedTask(string? status)
    {
        var task = await TaskOnAsync(TaskTestKit.Production);
        await _kit.SetStatusAsync(task.Id, TaskItemStatus.Blocked, "Waiting on supplies");
        var controller = ManagerOfProduction();
        var fields = new Dictionary<string, StringValues>(StringComparer.Ordinal);
        if (status is not null) fields["Status"] = status;
        var form = await BindAsync<StatusForm>(controller, nameof(TasksController.SetStatus), fields);
        _kit.Audit.ClearReceivedCalls();

        var result = await controller.SetStatus(task.Id, form, Ct);

        var saved = (await _kit.Service.GetTaskAsync(task.Id, Ct))!;
        if (string.Equals(status, "Todo", StringComparison.Ordinal))
        {
            result.Should().BeOfType<RedirectToActionResult>();
            saved.Status.Should().Be(TaskItemStatus.Todo);
            saved.BlockedNote.Should().BeNull();
        }
        else
        {
            result.Should().BeOfType<ViewResult>();
            saved.Status.Should().Be(TaskItemStatus.Blocked);
            saved.BlockedNote.Should().Be("Waiting on supplies");
            _kit.Audit.ReceivedCalls().Should().BeEmpty();
        }
    }

    private static async Task<T> BindAsync<T>(TasksController controller, string action, Dictionary<string, StringValues> fields)
    {
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddControllers();
        using var services = registrations.BuildServiceProvider();
        var previousServices = controller.HttpContext.RequestServices;
        controller.HttpContext.RequestServices = services;
        var parameter = typeof(TasksController).GetMethod(action)!.GetParameters()
            .Single(p => string.Equals(p.Name, "form", StringComparison.Ordinal));
        var metadata = ((ModelMetadataProvider)services.GetRequiredService<IModelMetadataProvider>()).GetMetadataForParameter(parameter);
        var binder = services.GetRequiredService<IModelBinderFactory>().CreateBinder(new ModelBinderFactoryContext { Metadata = metadata });
        var descriptor = new ControllerParameterDescriptor { Name = parameter.Name!, ParameterType = parameter.ParameterType, ParameterInfo = parameter };
        var binding = await services.GetRequiredService<ParameterBinder>().BindModelAsync(controller.ControllerContext, binder,
            new FormValueProvider(BindingSource.Form, new FormCollection(fields), CultureInfo.InvariantCulture), descriptor, metadata, value: null);
        controller.HttpContext.RequestServices = previousServices;
        return (T)binding.Model!;
    }

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
    public async Task Index_LinksAManagerToEachTeamTheyManage_EvenWithoutTasks()
    {
        static IReadOnlyList<TeamChoice> Teams(IActionResult r) =>
            r.Should().BeOfType<ViewResult>().Which.Model.Should().BeOfType<TaskListViewModel>().Which.ManagedTeams;

        Teams(await ManagerOfProduction().Index(new TaskListFilter(), Ct)).Should().ContainSingle().Which.Slug.Should().Be("production");
        Teams(await Human().Index(new TaskListFilter(), Ct)).Should().BeEmpty();
        Teams(await As([TaskTestKit.Retired]).Index(new TaskListFilter(), Ct)).Should().BeEmpty();
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

        var row = model.WaitingOn.Should().ContainSingle().Which;
        row.Title.Should().BeNull();
        row.Status.Should().BeNull();
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
    public async Task Team_TheAddFormStartsOnTheActiveEvent()
    {
        var result = await ManagerOfProduction().Team("production", new TaskListFilter(), Ct);

        result.Should().BeOfType<ViewResult>().Which.Model.Should().BeOfType<TeamPageViewModel>().Which.AddForm.EventId.Should().Be(TaskTestKit.Spring);
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
    public async Task Edit_AFieldThatDidNotBindIsRefused_NotTakenAsNone()
    {
        var task = await _kit.CreateAsync(TaskTestKit.Input(eventId: TaskTestKit.Spring));
        var form = TaskForm.From(task);
        form.EventId = null;
        var controller = ManagerOfProduction();
        controller.ModelState.AddModelError(nameof(TaskForm.EventId), "not a guid");

        var result = await controller.Edit(task.Id, form, Ct);

        var model = result.Should().BeOfType<ViewResult>().Which.Model.Should().BeOfType<TaskPageViewModel>().Which;
        model.EditOpen.Should().BeTrue();
        model.EditForm.Should().BeSameAs(form);
        (await _kit.Service.GetTaskAsync(task.Id, Ct))!.EventId.Should().Be(TaskTestKit.Spring);
    }

    [HumansFact]
    public async Task Add_AFieldThatDidNotBindIsRefused()
    {
        var controller = ManagerOfProduction();
        controller.ModelState.AddModelError(nameof(TaskForm.DueDate), "not a date");

        var result = await controller.Add("production", FormFor(TaskTestKit.Production), Ct);

        result.Should().BeOfType<ViewResult>();
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
    public async Task SetStatus_ARefusalShowsTheFormAgainWithWhatWasPicked()
    {
        var task = await TaskOnAsync(TaskTestKit.Production);
        var form = new StatusForm { Status = TaskItemStatus.Blocked, BlockedNote = " " };

        var result = await ManagerOfProduction().SetStatus(task.Id, form, Ct);

        result.Should().BeOfType<ViewResult>().Which.Model.Should().BeOfType<TaskPageViewModel>().Which.StatusForm.Should().BeSameAs(form);
        (await _kit.Service.GetTaskAsync(task.Id, Ct))!.Status.Should().Be(TaskItemStatus.Todo);
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
        _kit.Users.GetUserInfoAsync(someone, Arg.Any<CancellationToken>()).Returns((UserInfo?)null);

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

    private static async Task<(TasksController Controller, TaskPageViewModel Page)> PageAsync(TasksController controller, Guid id)
    {
        var page = (await controller.Details(id, Ct)).Should().BeOfType<ViewResult>().Which.Model.Should().BeOfType<TaskPageViewModel>().Which;
        return (controller, page);
    }

    private async Task<TaskInfo> RequestedAsync(Guid asker, int slots = 1)
    {
        var task = await _kit.CreateAsync(TaskTestKit.Input(slots: slots, needsApproval: true));
        (await _kit.Service.ClaimAsync(task.Id, asker, Ct)).Succeeded.Should().BeTrue();
        return task;
    }

    private async Task<TaskInfo> ReloadAsync(Guid id) => (await _kit.Service.GetTaskAsync(id, Ct))!;

    [HumansFact]
    public async Task Details_OffersTheRightButtonToTheRightPerson()
    {
        var free = await TaskOnAsync(TaskTestKit.Production);
        var full = await TaskOnAsync(TaskTestKit.Production, confirmed: Guid.NewGuid());
        var mine = await TaskOnAsync(TaskTestKit.Production, confirmed: _me);
        var asked = await RequestedAsync(_me);
        var finished = await TaskOnAsync(TaskTestKit.Production);
        await _kit.SetStatusAsync(finished.Id, TaskItemStatus.Done);

        (await PageAsync(Human(), free.Id)).Page.Place.Should().Be(MyPlace.CanClaim);
        (await PageAsync(Human(), full.Id)).Page.Place.Should().BeNull("a full task shows no claim button, not a disabled one");
        (await PageAsync(Human(), mine.Id)).Page.Place.Should().Be(MyPlace.Confirmed);
        (await PageAsync(Human(), asked.Id)).Page.Place.Should().Be(MyPlace.Requested);
        (await PageAsync(Human(), finished.Id)).Page.Place.Should().BeNull();
    }

    [HumansFact]
    public async Task Details_ShowsWaitingRequestsOnlyToManagers()
    {
        var task = await RequestedAsync(Guid.NewGuid());

        (await PageAsync(ManagerOfProduction(), task.Id)).Page.Requests.Should().ContainSingle();
        (await PageAsync(As([], RoleNames.Admin), task.Id)).Page.Requests.Should().BeEmpty("Admin without Tasks.Manage here is a plain viewer");
        (await PageAsync(As([], RoleNames.Board), task.Id)).Page.Requests.Should().BeEmpty();
        (await PageAsync(Human(), task.Id)).Page.Requests.Should().BeEmpty();
        (await PageAsync(As([TaskTestKit.Art]), task.Id)).Page.Requests.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Team_ListsRequestsOnlyToManagers()
    {
        static IReadOnlyList<RequestRow> Requests(IActionResult r) =>
            r.Should().BeOfType<ViewResult>().Which.Model.Should().BeOfType<TeamPageViewModel>().Which.Requests;

        var live = await RequestedAsync(Guid.NewGuid());
        var dead = await RequestedAsync(Guid.NewGuid());
        await _kit.SetStatusAsync(dead.Id, TaskItemStatus.Cancelled);

        Requests(await ManagerOfProduction().Team("production", new TaskListFilter(), Ct)).Select(r => r.Task.Id).Should().Equal(live.Id, dead.Id);
        (await PageAsync(ManagerOfProduction(), dead.Id)).Page.Requests.Should().ContainSingle("a cancelled task still owes the asker an answer");
        Requests(await Human().Team("production", new TaskListFilter(), Ct)).Should().BeEmpty();
        Requests(await As([], RoleNames.Board).Team("production", new TaskListFilter(), Ct)).Should().BeEmpty();
        Requests(await As([TaskTestKit.Art]).Team("production", new TaskListFilter(), Ct)).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Open_ListsFreeTasks_MostNeededFirst()
    {
        var hidden = await TaskOnAsync(TaskTestKit.Production, hidden: true);
        var full = await TaskOnAsync(TaskTestKit.Production, confirmed: Guid.NewGuid());
        var mine = await _kit.CreateAsync(TaskTestKit.Input(title: "Mine", slots: 2));
        (await _kit.Service.AssignAsync(mine.Id, _me, _kit.Actor, Ct)).Succeeded.Should().BeTrue();
        var asked = await RequestedAsync(_me);
        var finished = await TaskOnAsync(TaskTestKit.Production);
        await _kit.SetStatusAsync(finished.Id, TaskItemStatus.Done);
        var important = await _kit.CreateAsync(TaskTestKit.Input(title: "Important one"));
        var critical = await _kit.CreateAsync(TaskTestKit.Input(title: "Critical one") with { Priority = SlotPriority.Critical });

        var model = (await Human().Open(Ct)).Should().BeOfType<ViewResult>().Which.Model.Should().BeOfType<OpenBoardViewModel>().Which;

        model.Tasks.Select(t => t.Id).Should().Equal(critical.Id, asked.Id, important.Id, mine.Id);
        model.OpenSlots.Should().Be(4);
        model.CriticalCount.Should().Be(1);
        model.Places[mine.Id].Should().Be(MyPlace.Confirmed);
        model.Places[asked.Id].Should().Be(MyPlace.Requested);
        new[] { hidden.Id, full.Id, finished.Id }.Should().NotIntersectWith(model.Tasks.Select(t => t.Id));
        var asSeeAll = (await As([], RoleNames.Board).Open(Ct)).Should().BeOfType<ViewResult>().Which.Model.Should().BeOfType<OpenBoardViewModel>().Which;
        asSeeAll.Tasks.Select(t => t.Id).Should().Contain(hidden.Id);
    }

    [HumansFact]
    public async Task Claim_TakesSlotOrMakesRequest()
    {
        var plain = await TaskOnAsync(TaskTestKit.Production);
        var guarded = await _kit.CreateAsync(TaskTestKit.Input(needsApproval: true));
        var human = Human();

        (await human.Claim(plain.Id, null, Ct)).Should().BeOfType<RedirectToActionResult>();
        human.TempData[TempDataKeys.SuccessMessage].Should().Be("Tasks_Claimed");
        (await Human().Claim(guarded.Id, null, Ct)).Should().BeOfType<RedirectToActionResult>();

        (await ReloadAsync(plain.Id)).Assignments.Should().ContainSingle().Which.Should().Match<TaskAssignmentInfo>(a => a.UserId == _me && a.IsConfirmed);
        (await ReloadAsync(guarded.Id)).Assignments.Should().ContainSingle().Which.IsConfirmed.Should().BeFalse();
    }

    [HumansFact]
    public async Task Claim_HiddenTask_IsNotFound()
    {
        var hidden = await TaskOnAsync(TaskTestKit.Production, hidden: true);
        var full = await TaskOnAsync(TaskTestKit.Production, confirmed: Guid.NewGuid());
        var finished = await TaskOnAsync(TaskTestKit.Production);
        await _kit.SetStatusAsync(finished.Id, TaskItemStatus.Done);

        (await Human().Claim(hidden.Id, null, Ct)).Should().BeOfType<NotFoundResult>();
        (await Human().Claim(Guid.NewGuid(), null, Ct)).Should().BeOfType<NotFoundResult>();
        foreach (var task in new[] { full, finished })
        {
            var controller = Human();
            (await controller.Claim(task.Id, null, Ct)).Should().BeOfType<RedirectToActionResult>();
            controller.TempData.ContainsKey(TempDataKeys.ErrorMessage).Should().BeTrue();
        }

        (await ReloadAsync(hidden.Id)).Assignments.Should().BeEmpty();
        (await ReloadAsync(full.Id)).Assignments.Should().ContainSingle();
        (await ReloadAsync(finished.Id)).Assignments.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Claim_ReturnUrl_StaysOnThisSite()
    {
        var first = await TaskOnAsync(TaskTestKit.Production);
        var second = await TaskOnAsync(TaskTestKit.Production);

        (await Human().Claim(first.Id, "/Tasks/Open", Ct)).Should().BeOfType<LocalRedirectResult>().Which.Url.Should().Be("/Tasks/Open");
        (await Human().Claim(second.Id, "https://evil.example/x", Ct)).Should().BeOfType<RedirectToActionResult>();
    }

    [HumansFact]
    public async Task Release_GivesBackOnlyTheCallersOwnPlace()
    {
        var mine = await TaskOnAsync(TaskTestKit.Production, confirmed: _me);
        var other = Guid.NewGuid();
        var theirs = await TaskOnAsync(TaskTestKit.Production, confirmed: other);

        var stranger = Human();
        (await stranger.Release(theirs.Id, null, Ct)).Should().BeOfType<RedirectToActionResult>();
        stranger.TempData.ContainsKey(TempDataKeys.ErrorMessage).Should().BeTrue();
        (await ReloadAsync(theirs.Id)).Assignments.Should().ContainSingle(a => a.UserId == other);

        (await Human().Release(mine.Id, null, Ct)).Should().BeOfType<RedirectToActionResult>();
        (await ReloadAsync(mine.Id)).Assignments.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Release_HiddenTask_IsNotFound()
    {
        var hidden = await TaskOnAsync(TaskTestKit.Production, hidden: true, confirmed: Guid.NewGuid());

        (await Human().Release(hidden.Id, null, Ct)).Should().BeOfType<NotFoundResult>();
    }

    [HumansFact]
    public async Task Withdraw_TakesBackOnlyTheCallersOwnRequest()
    {
        var other = Guid.NewGuid();
        var theirs = await RequestedAsync(other);
        var mine = await RequestedAsync(_me);

        var stranger = Human();
        (await stranger.Withdraw(theirs.Id, null, Ct)).Should().BeOfType<RedirectToActionResult>();
        stranger.TempData.ContainsKey(TempDataKeys.ErrorMessage).Should().BeTrue();
        (await ReloadAsync(theirs.Id)).Assignments.Should().ContainSingle(a => a.UserId == other);

        (await Human().Withdraw(mine.Id, null, Ct)).Should().BeOfType<RedirectToActionResult>();
        (await ReloadAsync(mine.Id)).Assignments.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Withdraw_WorksOnATaskHiddenAfterTheyAsked()
    {
        var task = await _kit.CreateAsync(TaskTestKit.Input(needsApproval: true, hidden: true));
        (await _kit.Service.ClaimAsync(task.Id, _me, Ct)).Succeeded.Should().BeTrue();

        (await PageAsync(Human(), task.Id)).Page.Place.Should().Be(MyPlace.Requested);
        (await Human().Withdraw(task.Id, null, Ct)).Should().BeOfType<RedirectToActionResult>();

        (await ReloadAsync(task.Id)).Assignments.Should().BeEmpty();
        (await Human().Details(task.Id, Ct)).Should().BeOfType<NotFoundResult>();
    }

    [HumansFact]
    public async Task Withdraw_DoesNotTouchAConfirmedPlace()
    {
        var mine = await TaskOnAsync(TaskTestKit.Production, confirmed: _me);

        await Human().Withdraw(mine.Id, null, Ct);

        (await ReloadAsync(mine.Id)).Assignments.Should().ContainSingle().Which.IsConfirmed.Should().BeTrue();
    }

    [HumansFact]
    public async Task Approve_NeedsManageOnTheTasksTeam()
    {
        var asker = Guid.NewGuid();
        var task = await RequestedAsync(asker);

        (await Human().Approve(task.Id, asker, null, Ct)).Should().BeOfType<ForbidResult>();
        (await As([], RoleNames.Board).Approve(task.Id, asker, null, Ct)).Should().BeOfType<ForbidResult>();
        (await As([], RoleNames.Admin).Approve(task.Id, asker, null, Ct)).Should().BeOfType<ForbidResult>();
        (await As([TaskTestKit.Art]).Approve(task.Id, asker, null, Ct)).Should().BeOfType<ForbidResult>();
        (await ReloadAsync(task.Id)).Assignments.Should().ContainSingle().Which.IsConfirmed.Should().BeFalse();

        (await ManagerOfProduction().Approve(task.Id, asker, null, Ct)).Should().BeOfType<RedirectToActionResult>();
        (await ReloadAsync(task.Id)).Assignments.Should().ContainSingle().Which.IsConfirmed.Should().BeTrue();
    }

    [HumansFact]
    public async Task Approve_OtherTeamsTask_IsNotFound()
    {
        var asker = Guid.NewGuid();
        var task = await _kit.CreateAsync(TaskTestKit.Input(hidden: true, needsApproval: true));
        (await _kit.Service.ClaimAsync(task.Id, asker, Ct)).Succeeded.Should().BeTrue();

        (await As([TaskTestKit.Art]).Approve(task.Id, asker, null, Ct)).Should().BeOfType<NotFoundResult>();
    }

    [HumansFact]
    public async Task Decline_WithoutManage_IsForbidden()
    {
        var asker = Guid.NewGuid();
        _kit.GiveEmail(asker, "asker@example.org");
        var task = await RequestedAsync(asker);

        (await Human().Decline(task.Id, asker, "no", null, Ct)).Should().BeOfType<ForbidResult>();
        (await As([], RoleNames.Board).Decline(task.Id, asker, "no", null, Ct)).Should().BeOfType<ForbidResult>();
        (await As([TaskTestKit.Art]).Decline(task.Id, asker, "no", null, Ct)).Should().BeOfType<ForbidResult>();
        (await ReloadAsync(task.Id)).Assignments.Should().ContainSingle();
        _kit.SentEmails.Should().BeEmpty();

        (await ManagerOfProduction().Decline(task.Id, asker, "needs the licence", "/Tasks/Team/production", Ct)).Should().BeOfType<LocalRedirectResult>();
        (await ReloadAsync(task.Id)).Assignments.Should().BeEmpty();
        _kit.SentEmails.Should().ContainSingle();
    }

    [HumansFact]
    public async Task Decline_TooLongReason_KeepsRequest()
    {
        var asker = Guid.NewGuid();
        var task = await RequestedAsync(asker);
        var manager = ManagerOfProduction();

        await manager.Decline(task.Id, asker, new string('x', 201), null, Ct);

        manager.TempData.ContainsKey(TempDataKeys.ErrorMessage).Should().BeTrue();
        (await ReloadAsync(task.Id)).Assignments.Should().ContainSingle();
    }

    [HumansFact]
    public async Task Assign_IsNotOfferedForSomeoneWhoAlreadyAsked()
    {
        var asker = Guid.NewGuid();
        var task = await RequestedAsync(asker);

        var manager = ManagerOfProduction();
        await manager.Assign(task.Id, asker, Ct);

        manager.TempData.ContainsKey(TempDataKeys.ErrorMessage).Should().BeTrue();
        (await ReloadAsync(task.Id)).Assignments.Should().ContainSingle().Which.IsConfirmed.Should().BeFalse();
    }

    [HumansFact]
    public async Task Index_MarksTheViewersPlaceOnEachTask()
    {
        var free = await TaskOnAsync(TaskTestKit.Production);
        var asked = await RequestedAsync(_me);

        var model = (await Human().Index(new TaskListFilter(), Ct)).Should().BeOfType<ViewResult>().Which.Model.Should().BeOfType<TaskListViewModel>().Which;

        model.Places[free.Id].Should().Be(MyPlace.CanClaim);
        model.Places[asked.Id].Should().Be(MyPlace.Requested);
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
