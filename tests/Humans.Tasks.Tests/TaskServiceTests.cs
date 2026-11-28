using AwesomeAssertions;
using Humans.Audit.Contracts;
using Humans.Tasks.Domain;
using Humans.Tasks.Services;
using Humans.Users.Contracts;
using NodaTime;
using NSubstitute;
using Xunit;
using static Humans.Tasks.Tests.TaskTestKit;

namespace Humans.Tasks.Tests;

public sealed class TaskServiceTests
{
    private readonly TaskTestKit _kit = new();

    [HumansFact]
    public async Task Create_StoresARelativeDateAsDaysFromGateOpening()
    {
        var task = await _kit.CreateAsync(Input(
            eventId: Spring,
            start: new(IsRelative: true, SpringGate.PlusDays(-4)),
            due: new(IsRelative: true, SpringGate.PlusDays(2))));

        task.Start!.DayOffset.Should().Be(-4);
        task.Start.Fixed.Should().BeNull();
        task.Start.Resolved.Should().Be(SpringGate.PlusDays(-4));
        task.Due!.DayOffset.Should().Be(2);
        task.Status.Should().Be(TaskItemStatus.Todo);
    }

    [HumansFact]
    public async Task Create_StoresAFixedDateAsTheDate()
    {
        var date = new LocalDate(2027, 2, 1);

        var task = await _kit.CreateAsync(Input(due: new(IsRelative: false, date)));

        task.Due!.Fixed.Should().Be(date);
        task.Due.DayOffset.Should().BeNull();
        task.Due.IsRelative.Should().BeFalse();
        task.Start.Should().BeNull();
    }

    [HumansFact]
    public async Task Create_RefusesARelativeDateWithoutAnEvent()
    {
        var result = await _kit.Service.CreateAsync(
            Input(start: new(IsRelative: true, SpringGate)), _kit.Actor, Ct);

        result.Refusal.Should().Be("Tasks_Refusal_EventNeededForRelative");
    }

    [HumansFact]
    public async Task Create_RefusesADueDateBeforeTheStart()
    {
        var result = await _kit.Service.CreateAsync(
            Input(start: new(false, new(2027, 3, 5)), due: new(false, new(2027, 3, 1))), _kit.Actor, Ct);

        result.Refusal.Should().Be("Tasks_Refusal_DueBeforeStart");
    }

    [HumansTheory]
    [InlineData(TeamKind.Retired)]
    [InlineData(TeamKind.System)]
    [InlineData(TeamKind.Unknown)]
    public async Task Create_OnlyActiveNonSystemTeamsCanHaveTasks(TeamKind kind)
    {
        var team = kind switch
        {
            TeamKind.Retired => Retired,
            TeamKind.System => Everyone,
            _ => Guid.NewGuid(),
        };

        var result = await _kit.Service.CreateAsync(Input(team: team), _kit.Actor, Ct);

        result.Refusal.Should().Be("Tasks_Refusal_NoTeam");
    }

    public enum TeamKind
    {
        Retired,
        System,
        Unknown,
    }

    [HumansFact]
    public async Task Create_RefusesAWithdrawnEvent()
    {
        var result = await _kit.Service.CreateAsync(Input(eventId: Withdrawn), _kit.Actor, Ct);

        result.Refusal.Should().Be("Tasks_Refusal_NoEvent");
    }

    [HumansFact]
    public async Task Create_RefusesBadInput()
    {
        (await _kit.Service.CreateAsync(Input(title: "  "), _kit.Actor, Ct)).Refusal.Should().Be("Tasks_Refusal_NoTitle");
        (await _kit.Service.CreateAsync(Input(slots: 0), _kit.Actor, Ct)).Refusal.Should().Be("Tasks_Refusal_NoSlots");
        (await _kit.Service.CreateAsync(Input(title: new string('x', 201)), _kit.Actor, Ct)).Refusal.Should().Be("Tasks_Refusal_TitleTooLong");
    }

    [HumansFact]
    public async Task Create_DropsTheApprovalReasonWhenApprovalIsOff()
    {
        var off = await _kit.CreateAsync(Input(needsApproval: false, reason: "chainsaw licence"));
        var on = await _kit.CreateAsync(Input(needsApproval: true, reason: "chainsaw licence"));

        off.ApprovalReason.Should().BeNull();
        on.ApprovalReason.Should().Be("chainsaw licence");
    }

    [HumansFact]
    public async Task Update_ChangingTheEventKeepsTheDayNumbers()
    {
        var task = await _kit.CreateAsync(Input(eventId: Spring, due: new(true, SpringGate.PlusDays(-3))));

        // The form posts the date as the old event shows it.
        var result = await _kit.Service.UpdateAsync(
            task.Id, Input(eventId: Autumn, due: new(true, SpringGate.PlusDays(-3))), _kit.Actor, Ct);

        result.Task!.Due!.DayOffset.Should().Be(-3);
        result.Task.Due.Resolved.Should().Be(AutumnGate.PlusDays(-3));
        result.Task.EventName.Should().Be("Autumn");
    }

    [HumansFact]
    public async Task Update_APickedDateIsWorkedOutFromTheNewEvent()
    {
        var task = await _kit.CreateAsync(Input(eventId: Spring, due: new(true, SpringGate.PlusDays(-3))));

        var result = await _kit.Service.UpdateAsync(
            task.Id, Input(eventId: Autumn, due: new(true, AutumnGate.PlusDays(5))), _kit.Actor, Ct);

        result.Task!.Due!.DayOffset.Should().Be(5);
    }

    [HumansFact]
    public async Task Update_CannotRemoveTheEventWhileADateIsRelative()
    {
        var task = await _kit.CreateAsync(Input(eventId: Spring, due: new(true, SpringGate)));

        var result = await _kit.Service.UpdateAsync(
            task.Id, Input(eventId: null, due: new(true, SpringGate)), _kit.Actor, Ct);

        result.Refusal.Should().Be("Tasks_Refusal_EventNeededForRelative");
    }

    [HumansFact]
    public async Task Update_MovesATaskToAnotherTeamAndAuditsIt()
    {
        var task = await _kit.CreateAsync();

        var result = await _kit.Service.UpdateAsync(task.Id, Input(team: Art), _kit.Actor, Ct);

        result.Task!.Owner.Id.Should().Be(Art);
        result.Task.TeamName.Should().Be("Art");
        await _kit.Audit.Received().LogAsync(
            TasksAuditActions.TaskUpdated, "TaskItem", task.Id,
            Arg.Is<string>(d => d.Contains("moved from Production to Art", StringComparison.Ordinal)),
            _kit.Actor, Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task SetStatus_BlockedNeedsANoteAndLeavingBlockedClearsIt()
    {
        var task = await _kit.CreateAsync();

        (await _kit.Service.SetStatusAsync(task.Id, TaskItemStatus.Blocked, "  ", _kit.Actor, Ct))
            .Refusal.Should().Be("Tasks_Refusal_NoteNeeded");

        var blocked = await _kit.Service.SetStatusAsync(task.Id, TaskItemStatus.Blocked, " waiting for the permit ", _kit.Actor, Ct);
        blocked.Task!.BlockedNote.Should().Be("waiting for the permit");

        var started = await _kit.Service.SetStatusAsync(task.Id, TaskItemStatus.InProgress, "ignored", _kit.Actor, Ct);
        started.Task!.BlockedNote.Should().BeNull();
        (await _kit.Service.GetTaskAsync(task.Id, Ct))!.BlockedNote.Should().BeNull();
    }

    [HumansFact]
    public async Task IsWaiting_WhileSomethingItWaitsOnIsNotFinished()
    {
        var waiting = await _kit.CreateAsync(Input(title: "Paint"));
        var first = await _kit.CreateAsync(Input(title: "Build"));
        (await _kit.Service.AddDependencyAsync(waiting.Id, first.Id, _kit.Actor, Ct)).Succeeded.Should().BeTrue();

        (await _kit.Service.GetTaskAsync(waiting.Id, Ct))!.IsWaiting.Should().BeTrue();

        await _kit.SetStatusAsync(first.Id, TaskItemStatus.InProgress);
        (await _kit.Service.GetTaskAsync(waiting.Id, Ct))!.IsWaiting.Should().BeTrue();

        await _kit.SetStatusAsync(first.Id, TaskItemStatus.Done);
        (await _kit.Service.GetTaskAsync(waiting.Id, Ct))!.IsWaiting.Should().BeFalse();
    }

    [HumansFact]
    public async Task IsWaiting_ACancelledTaskHoldsNothingUp()
    {
        var waiting = await _kit.CreateAsync(Input(title: "Paint"));
        var first = await _kit.CreateAsync(Input(title: "Build"));
        await _kit.Service.AddDependencyAsync(waiting.Id, first.Id, _kit.Actor, Ct);

        await _kit.SetStatusAsync(first.Id, TaskItemStatus.Cancelled);

        (await _kit.Service.GetTaskAsync(waiting.Id, Ct))!.IsWaiting.Should().BeFalse();
    }

    [HumansFact]
    public async Task IsWaiting_AFinishedTaskWaitsOnNothing()
    {
        var waiting = await _kit.CreateAsync(Input(title: "Paint"));
        var first = await _kit.CreateAsync(Input(title: "Build"));
        await _kit.Service.AddDependencyAsync(waiting.Id, first.Id, _kit.Actor, Ct);

        await _kit.SetStatusAsync(waiting.Id, TaskItemStatus.Done);

        (await _kit.Service.GetTaskAsync(waiting.Id, Ct))!.IsWaiting.Should().BeFalse();
    }

    [HumansFact]
    public async Task IsWaiting_IsNotTheStoredBlockedStatus()
    {
        var waiting = await _kit.CreateAsync(Input(title: "Paint"));
        var first = await _kit.CreateAsync(Input(title: "Build"));
        await _kit.Service.AddDependencyAsync(waiting.Id, first.Id, _kit.Actor, Ct);

        var info = (await _kit.Service.GetTaskAsync(waiting.Id, Ct))!;

        info.IsWaiting.Should().BeTrue();
        info.Status.Should().Be(TaskItemStatus.Todo);
        // Dependencies are shown, never enforced: a waiting task can still be started and finished.
        (await _kit.Service.SetStatusAsync(waiting.Id, TaskItemStatus.Done, null, _kit.Actor, Ct)).Succeeded.Should().BeTrue();
    }

    [HumansFact]
    public async Task Dependencies_RefuseSelfDuplicateAndCircles()
    {
        var a = await _kit.CreateAsync(Input(title: "A"));
        var b = await _kit.CreateAsync(Input(title: "B", team: Art));
        var c = await _kit.CreateAsync(Input(title: "C"));

        (await _kit.Service.AddDependencyAsync(a.Id, a.Id, _kit.Actor, Ct)).Refusal.Should().Be("Tasks_Refusal_DependsOnSelf");
        (await _kit.Service.AddDependencyAsync(a.Id, b.Id, _kit.Actor, Ct)).Succeeded.Should().BeTrue();
        (await _kit.Service.AddDependencyAsync(a.Id, b.Id, _kit.Actor, Ct)).Refusal.Should().Be("Tasks_Refusal_DependencyExists");
        (await _kit.Service.AddDependencyAsync(b.Id, c.Id, _kit.Actor, Ct)).Succeeded.Should().BeTrue();

        (await _kit.Service.AddDependencyAsync(b.Id, a.Id, _kit.Actor, Ct)).Refusal.Should().Be("Tasks_Refusal_DependencyCycle");
        (await _kit.Service.AddDependencyAsync(c.Id, a.Id, _kit.Actor, Ct)).Refusal.Should().Be("Tasks_Refusal_DependencyCycle");
    }

    [HumansFact]
    public async Task Dependencies_CanBeRemoved_AndAMissingOneIsRefused()
    {
        var a = await _kit.CreateAsync(Input(title: "A"));
        var b = await _kit.CreateAsync(Input(title: "B"));
        await _kit.Service.AddDependencyAsync(a.Id, b.Id, _kit.Actor, Ct);

        (await _kit.Service.RemoveDependencyAsync(a.Id, b.Id, _kit.Actor, Ct)).Task!.WaitingOn.Should().BeEmpty();
        (await _kit.Service.RemoveDependencyAsync(a.Id, b.Id, _kit.Actor, Ct)).Refusal.Should().Be("Tasks_Refusal_NoDependency");
    }

    [HumansFact]
    public async Task Delete_RemovesTheTaskAndEveryLinkToIt()
    {
        var a = await _kit.CreateAsync(Input(title: "A"));
        var b = await _kit.CreateAsync(Input(title: "B"));
        await _kit.Service.AddDependencyAsync(a.Id, b.Id, _kit.Actor, Ct);

        var result = await _kit.Service.DeleteAsync(b.Id, _kit.Actor, Ct);

        result.Removed!.Title.Should().Be("B");
        (await _kit.Service.GetTaskAsync(b.Id, Ct)).Should().BeNull();
        (await _kit.Service.GetTaskAsync(a.Id, Ct))!.WaitingOn.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Assign_AManagerCanOverfillAndNothingChangesTheSlotCount()
    {
        var task = await _kit.CreateAsync(Input(slots: 4));
        TaskInfo? info = null;
        for (var i = 0; i < 5; i++)
            info = (await _kit.Service.AssignAsync(task.Id, Guid.NewGuid(), _kit.Actor, Ct)).Task;

        info!.ConfirmedCount.Should().Be(5);
        info.SlotCount.Should().Be(4);
        info.OpenSlots.Should().Be(0);
    }

    [HumansFact]
    public async Task Update_SlotsCanDropBelowThePeopleOnTheTask()
    {
        var task = await _kit.CreateAsync(Input(slots: 3));
        for (var i = 0; i < 3; i++)
            await _kit.Service.AssignAsync(task.Id, Guid.NewGuid(), _kit.Actor, Ct);

        var result = await _kit.Service.UpdateAsync(task.Id, Input(slots: 1), _kit.Actor, Ct);

        result.Task!.SlotCount.Should().Be(1);
        result.Task.ConfirmedCount.Should().Be(3);
    }

    [HumansFact]
    public async Task Assign_RefusesSomeoneAlreadyOnAndAnUnknownPerson()
    {
        var task = await _kit.CreateAsync(Input(slots: 2));
        var person = Guid.NewGuid();
        var nobody = Guid.NewGuid();
        _kit.Users.GetUserInfoAsync(nobody, Arg.Any<CancellationToken>()).Returns((UserInfo?)null);

        (await _kit.Service.AssignAsync(task.Id, person, _kit.Actor, Ct)).Task!.OpenSlots.Should().Be(1);
        (await _kit.Service.AssignAsync(task.Id, person, _kit.Actor, Ct)).Refusal.Should().Be("Tasks_Refusal_AlreadyOn");
        (await _kit.Service.AssignAsync(task.Id, nobody, _kit.Actor, Ct)).Refusal.Should().Be("Tasks_Refusal_NoPerson");
        (await _kit.Service.GetTaskAsync(task.Id, Ct))!.Assignments.Should().ContainSingle();
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Assign_Tombstone_DoesNotRecreateErasedLink(bool legacy)
    {
        var task = await _kit.CreateAsync();
        var personId = Guid.NewGuid();
        await _kit.Service.AssignAsync(task.Id, personId, _kit.Actor, Ct);
        await _kit.Service.EraseForUserAsync(personId, Ct);
        var person = (await _kit.Users.GetUserInfoAsync(personId, Ct))!;
        person = legacy ? person with { IdentityEmailColumn = "erased@gdpr.local" } : person with { State = UserState.Deleted };
        _kit.Users.GetUserInfoAsync(personId, Arg.Any<CancellationToken>()).Returns(person);
        _kit.Audit.ClearReceivedCalls();

        var result = await _kit.Service.AssignAsync(task.Id, personId, _kit.Actor, Ct);

        result.Refusal.Should().Be("Tasks_Refusal_NoPerson");
        (await _kit.Service.GetTaskAsync(task.Id, Ct))!.Assignments.Should().BeEmpty();
        _kit.Audit.ReceivedCalls().Should().BeEmpty();
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Assign_ArchivedId_UsesSurvivor(bool alreadyAssigned)
    {
        var task = await _kit.CreateAsync();
        var archivedId = Guid.NewGuid();
        var survivorId = Guid.NewGuid();
        var survivor = (await _kit.Users.GetUserInfoAsync(survivorId, Ct))! with { MergedUserIds = [archivedId] };
        _kit.Users.GetUserInfoAsync(archivedId, Arg.Any<CancellationToken>()).Returns(survivor);
        if (alreadyAssigned) await _kit.Service.AssignAsync(task.Id, survivorId, _kit.Actor, Ct);
        _kit.Audit.ClearReceivedCalls();

        var result = await _kit.Service.AssignAsync(task.Id, archivedId, _kit.Actor, Ct);

        if (alreadyAssigned)
        {
            result.Refusal.Should().Be("Tasks_Refusal_AlreadyOn");
            _kit.Audit.ReceivedCalls().Should().BeEmpty();
        }
        else
        {
            result.Succeeded.Should().BeTrue();
            await _kit.Audit.Received(1).LogAsync(TasksAuditActions.TaskAssigned, TaskService.AuditSubjectType, task.Id,
                Arg.Any<string>(), _kit.Actor, survivorId, AuditSubjectTypes.User);
        }
        (await _kit.Service.GetTaskAsync(task.Id, Ct))!.Assignments.Should().ContainSingle().Which.UserId.Should().Be(survivorId);
    }

    [HumansFact]
    public async Task Unassign_RemovesThePersonAndRefusesSomeoneNotOnTheTask()
    {
        var task = await _kit.CreateAsync();
        var person = Guid.NewGuid();
        await _kit.Service.AssignAsync(task.Id, person, _kit.Actor, Ct);

        (await _kit.Service.UnassignAsync(task.Id, person, _kit.Actor, Ct)).Task!.Assignments.Should().BeEmpty();
        (await _kit.Service.UnassignAsync(task.Id, person, _kit.Actor, Ct)).Refusal.Should().Be("Tasks_Refusal_NotOn");
    }

    [HumansFact]
    public async Task EveryChangeWritesAnAuditEntry()
    {
        var a = await _kit.CreateAsync(Input(title: "A"));
        var b = await _kit.CreateAsync(Input(title: "B"));
        var person = Guid.NewGuid();
        await _kit.Service.UpdateAsync(a.Id, Input(title: "A2"), _kit.Actor, Ct);
        await _kit.Service.SetStatusAsync(a.Id, TaskItemStatus.Blocked, "permit", _kit.Actor, Ct);
        await _kit.Service.AssignAsync(a.Id, person, _kit.Actor, Ct);
        await _kit.Service.UnassignAsync(a.Id, person, _kit.Actor, Ct);
        await _kit.Service.AddDependencyAsync(a.Id, b.Id, _kit.Actor, Ct);
        await _kit.Service.RemoveDependencyAsync(a.Id, b.Id, _kit.Actor, Ct);
        await _kit.Service.DeleteAsync(b.Id, _kit.Actor, Ct);

        foreach (var action in new[]
                 {
                     TasksAuditActions.TaskCreated, TasksAuditActions.TaskUpdated, TasksAuditActions.TaskStatusChanged,
                     TasksAuditActions.TaskAssigned, TasksAuditActions.TaskUnassigned,
                     TasksAuditActions.TaskDependencyAdded, TasksAuditActions.TaskDependencyRemoved, TasksAuditActions.TaskDeleted,
                 })
        {
            await _kit.Audit.Received().LogAsync(
                action, Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(), _kit.Actor, Arg.Any<Guid?>(), Arg.Any<string?>());
        }
    }

    [HumansFact]
    public async Task ARefusedWriteWritesNoAuditEntry()
    {
        await _kit.Service.CreateAsync(Input(title: ""), _kit.Actor, Ct);
        await _kit.Service.DeleteAsync(Guid.NewGuid(), _kit.Actor, Ct);

        await _kit.Audit.DidNotReceiveWithAnyArgs().LogAsync(
            default!, default!, default, default!, default(Guid), default, default);
    }

    [HumansFact]
    public async Task Listed_IsTheActiveEventsTasksAndEventlessOnes_WithoutFinishedOnes()
    {
        var spring = await _kit.CreateAsync(Input(title: "Spring", eventId: Spring));
        var none = await _kit.CreateAsync(Input(title: "No event"));
        var autumn = await _kit.CreateAsync(Input(title: "Autumn", eventId: Autumn));
        var done = await _kit.CreateAsync(Input(title: "Done", eventId: Spring));
        await _kit.SetStatusAsync(done.Id, TaskItemStatus.Done);

        var all = await _kit.Service.GetTasksAsync(Ct);
        var listed = _kit.Service.Listed(all);

        listed.Select(t => t.Id).Should().BeEquivalentTo([spring.Id, none.Id]);
        all.Select(t => t.Id).Should().Contain([autumn.Id, done.Id]);
    }

    [HumansFact]
    public async Task Listed_AScopeWidensOrNarrowsTheDefault()
    {
        var spring = await _kit.CreateAsync(Input(title: "Spring", eventId: Spring));
        var none = await _kit.CreateAsync(Input(title: "No event"));
        var autumn = await _kit.CreateAsync(Input(title: "Autumn", eventId: Autumn));
        var done = await _kit.CreateAsync(Input(title: "Done", eventId: Spring));
        await _kit.SetStatusAsync(done.Id, TaskItemStatus.Done);

        async Task<IEnumerable<Guid>> IdsAsync(TaskListScope scope) =>
            _kit.Service.Listed(await _kit.Service.GetTasksAsync(Ct), scope).Select(t => t.Id);

        (await IdsAsync(new(IncludeFinished: true))).Should().BeEquivalentTo([spring.Id, none.Id, done.Id]);
        (await IdsAsync(new(AnyEvent: true))).Should().BeEquivalentTo([spring.Id, none.Id, autumn.Id]);
        (await IdsAsync(new(EventId: Autumn))).Should().BeEquivalentTo([autumn.Id]);
    }

    [HumansFact]
    public async Task AdminSummary_CountsTheListedSetPerTeam_AndListsEveryBlockedTask()
    {
        await _kit.CreateAsync(Input(title: "One", team: Production, slots: 3));
        var two = await _kit.CreateAsync(Input(title: "Two", team: Production, slots: 2));
        await _kit.Service.AssignAsync(two.Id, Guid.NewGuid(), _kit.Actor, Ct);
        await _kit.Service.AssignAsync(two.Id, Guid.NewGuid(), _kit.Actor, Ct);
        await _kit.Service.AssignAsync(two.Id, Guid.NewGuid(), _kit.Actor, Ct);
        await _kit.SetStatusAsync(two.Id, TaskItemStatus.Blocked, "permit");
        var art = await _kit.CreateAsync(Input(title: "Art", team: Art, hidden: true));
        await _kit.SetStatusAsync(art.Id, TaskItemStatus.Blocked, "paint");
        var finished = await _kit.CreateAsync(Input(title: "Finished", team: Art));
        await _kit.SetStatusAsync(finished.Id, TaskItemStatus.Done);

        var summary = await _kit.Service.GetAdminSummaryAsync(Ct);

        summary.Teams.Select(t => t.TeamName).Should().BeEquivalentTo(["Art", "Production"]);
        var production = summary.Teams.Single(t => string.Equals(t.TeamName, "Production", StringComparison.Ordinal));
        production.Tasks.Should().Be(2);
        production.OpenSlots.Should().Be(3, because: "3 for the first task, never below zero for the overfilled one");
        production.BlockedTasks.Should().Be(1);
        summary.Teams.Single(t => string.Equals(t.TeamName, "Art", StringComparison.Ordinal)).Tasks.Should().Be(1);
        summary.BlockedTasks.Select(t => t.Title).Should().BeEquivalentTo(["Art", "Two"]);
        summary.BlockedTasks.Should().OnlyContain(t => t.BlockedNote != null);
    }

    [HumansFact]
    public async Task ATaskOfAHiddenTeamCountsAsHidden()
    {
        var task = await _kit.CreateAsync(Input(team: HiddenTeam));

        task.TeamIsHidden.Should().BeTrue();
        task.IsHidden.Should().BeFalse();
        task.IsEffectivelyHidden.Should().BeTrue();
    }
}
