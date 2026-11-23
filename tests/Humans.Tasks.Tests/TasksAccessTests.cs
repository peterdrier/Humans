using AwesomeAssertions;
using Humans.Base.Authorization;
using Humans.Tasks.Authorization;
using Humans.Tasks.Domain;
using Humans.Tasks.Services;
using Humans.Teams.Contracts;
using NodaTime;
using Xunit;

namespace Humans.Tasks.Tests;

/// <summary>The who-can-do-what table, over the facts a controller gathers from <c>IPermissionService</c>.</summary>
public sealed class TasksAccessTests
{
    private static readonly Guid Production = Guid.NewGuid();
    private static readonly Guid Art = Guid.NewGuid();
    private static readonly Guid Me = Guid.NewGuid();

    public enum Next
    {
        Todo,
        InProgress,
        Blocked,
        Done,
        Cancelled,
    }

    public enum Who
    {
        Human,
        ConfirmedOnTask,
        RequestedOnly,
        Manager,
        ManagerOfOtherTeam,
        SeesAll,
    }

    private static TasksAccess Access(Who who) => who switch
    {
        Who.Manager => new TasksAccess(Me, SeesAll: false, new HashSet<Guid> { Production }),
        Who.ManagerOfOtherTeam => new TasksAccess(Me, SeesAll: false, new HashSet<Guid> { Art }),
        Who.SeesAll => new TasksAccess(Me, SeesAll: true, new HashSet<Guid>()),
        _ => new TasksAccess(Me, SeesAll: false, new HashSet<Guid>()),
    };

    private static TaskInfo Task(Who who, bool hidden = false, bool teamHidden = false) => new(
        Guid.NewGuid(), new Owner(TeamOwner.Kind, Production), "Production", "production", teamHidden, "Build", null,
        null, null, null, null, TaskItemStatus.Todo, null, SlotPriority.Important, 1, false, null, hidden,
        who switch
        {
            Who.ConfirmedOnTask => [new TaskAssignmentInfo(Me, IsConfirmed: true, Instant.MinValue)],
            Who.RequestedOnly => [new TaskAssignmentInfo(Me, IsConfirmed: false, Instant.MinValue)],
            _ => [],
        },
        [], Instant.MinValue, Instant.MinValue);

    [HumansTheory]
    [InlineData(Who.Human, false, false, true)]
    [InlineData(Who.Human, true, false, false)]
    [InlineData(Who.Human, false, true, false)]
    [InlineData(Who.RequestedOnly, true, false, false)]
    [InlineData(Who.ConfirmedOnTask, true, false, true)]
    [InlineData(Who.ConfirmedOnTask, false, true, true)]
    [InlineData(Who.Manager, true, false, true)]
    [InlineData(Who.ManagerOfOtherTeam, true, false, false)]
    [InlineData(Who.SeesAll, true, true, true)]
    public void CanSee_HiddenTasksShowOnlyToTheirPeople(Who who, bool hidden, bool teamHidden, bool expected) =>
        Access(who).CanSee(Task(who, hidden, teamHidden)).Should().Be(expected);

    [HumansTheory]
    [InlineData(Who.Human, Next.Done, false)]
    [InlineData(Who.RequestedOnly, Next.Done, false)]
    [InlineData(Who.ManagerOfOtherTeam, Next.Done, false)]
    [InlineData(Who.SeesAll, Next.Done, false)]
    [InlineData(Who.ConfirmedOnTask, Next.Todo, true)]
    [InlineData(Who.ConfirmedOnTask, Next.InProgress, true)]
    [InlineData(Who.ConfirmedOnTask, Next.Blocked, true)]
    [InlineData(Who.ConfirmedOnTask, Next.Done, true)]
    [InlineData(Who.ConfirmedOnTask, Next.Cancelled, false)]
    [InlineData(Who.Manager, Next.Cancelled, true)]
    public void CanSetStatus_ConfirmedPeopleNeverCancel(Who who, Next status, bool expected) =>
        Access(who).CanSetStatus(Task(who), (TaskItemStatus)status).Should().Be(expected);

    [HumansTheory]
    [InlineData(Who.Human, false)]
    [InlineData(Who.ConfirmedOnTask, false)]
    [InlineData(Who.SeesAll, false)]
    [InlineData(Who.ManagerOfOtherTeam, false)]
    [InlineData(Who.Manager, true)]
    public void Manages_OnlyHoldersOfTheTeamsPermission(Who who, bool expected) =>
        Access(who).Manages(Task(who)).Should().Be(expected);

    [HumansFact]
    public void CanMove_NeedsTheManagePermissionOnBothTeams()
    {
        var task = Task(Who.Manager);

        new TasksAccess(Me, false, new HashSet<Guid> { Production, Art }).CanMove(task, Art).Should().BeTrue();
        new TasksAccess(Me, false, new HashSet<Guid> { Production }).CanMove(task, Art).Should().BeFalse();
        new TasksAccess(Me, false, new HashSet<Guid> { Art }).CanMove(task, Art).Should().BeFalse();
    }

    [HumansFact]
    public void Manages_IgnoresOtherOwnerKinds()
    {
        var task = Task(Who.Manager) with { Owner = new Owner("Camp", Production) };

        Access(Who.Manager).Manages(task).Should().BeFalse();
    }

    [HumansTheory]
    [InlineData(Who.Human, false, true)]
    [InlineData(Who.Human, true, false)]
    [InlineData(Who.ConfirmedOnTask, true, true)]
    [InlineData(Who.RequestedOnly, true, false)]
    [InlineData(Who.Manager, true, true)]
    [InlineData(Who.ManagerOfOtherTeam, true, false)]
    [InlineData(Who.SeesAll, true, true)]
    public void CanSeeTeam_AHiddenTeamIsFoundOnlyByItsPeople(Who who, bool teamHidden, bool expected) =>
        Access(who).CanSeeTeam(Production, teamHidden, [Task(who)]).Should().Be(expected);
}
