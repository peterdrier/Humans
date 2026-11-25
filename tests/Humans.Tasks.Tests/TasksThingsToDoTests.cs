using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base.Authorization;
using Humans.Base.Interfaces;
using Humans.Permissions.Contracts;
using Humans.Tasks.Contracts;
using Humans.Teams.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NSubstitute;
using Xunit;
using static Humans.Tasks.Tests.TaskTestKit;

namespace Humans.Tasks.Tests;

/// <summary>The waiting-requests entries on Home, for whoever holds <c>Tasks.Manage</c>.</summary>
public sealed class TasksThingsToDoTests
{
    private readonly TaskTestKit _kit = new();
    private readonly IPermissionService _permissions = Substitute.For<IPermissionService>();
    private readonly Guid _manager = Guid.NewGuid();
    private readonly Guid _sam = Guid.NewGuid();

    private async Task<IReadOnlyList<ThingsToDoEntry>> EntriesAsync(Guid? userId, params Guid[] managedTeams)
    {
        _permissions.GetOwnersAsync(Arg.Any<Guid>(), TasksPermissions.Manage, Arg.Any<CancellationToken>())
            .Returns(managedTeams.Select(t => new Owner(TeamOwner.Kind, t)).ToList());
        using var services = new ServiceCollection()
            .AddSingleton(_permissions)
            .AddSingleton<Humans.Tasks.Services.ITaskService>(_kit.Service)
            .AddSingleton<IStringLocalizer<TasksResource>>(new TaskTestKit.ResourceLocalizer())
            .BuildServiceProvider();
        var principal = userId is { } id
            ? new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id.ToString())], "test"))
            : new ClaimsPrincipal(new ClaimsIdentity());

        return (await ((ISectionThingsToDo)new Section()).EntriesAsync(services, principal, Ct)).ToList();
    }

    private async Task<Guid> RequestAsync(Guid team, string title, bool hidden = false)
    {
        var task = await _kit.CreateAsync(Input(team: team, title: title, slots: 2, needsApproval: true, hidden: hidden));
        await _kit.Service.ClaimAsync(task.Id, _sam, Ct);
        return task.Id;
    }

    [HumansFact]
    public async Task Manager_SeesTheirTeamsRequests()
    {
        await RequestAsync(Production, "Bar");
        var second = await RequestAsync(Production, "Stage");
        await _kit.Service.ClaimAsync(second, Guid.NewGuid(), Ct);

        var entry = (await EntriesAsync(_manager, Production)).Should().ContainSingle().Subject;

        entry.Key.Should().Be($"tasks-requests-{Production}");
        entry.RawHref.Should().Be("/Tasks/Team/production");
        entry.Description.Should().Contain("Production").And.Contain("3");
        entry.Text.Should().Be("Task requests waiting");
        entry.ActionText.Should().NotBeNullOrEmpty();
        entry.IsDone.Should().BeFalse();
    }

    [HumansFact]
    public async Task AManager_DoesNotSeeAnotherTeamsRequests()
    {
        await RequestAsync(Art, "Mural");

        (await EntriesAsync(_manager, Production)).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Admins_SeeEveryTeamsRequests()
    {
        await RequestAsync(Production, "Bar");
        await RequestAsync(Art, "Mural");

        var entries = await EntriesAsync(_manager, Production, Art, HiddenTeam, Everyone);

        entries.Select(e => e.Key).Should().BeEquivalentTo($"tasks-requests-{Production}", $"tasks-requests-{Art}");
    }

    [HumansFact]
    public async Task NonManager_SeesNothing()
    {
        await RequestAsync(Production, "Bar");

        (await EntriesAsync(_manager)).Should().BeEmpty();
    }

    [HumansFact]
    public async Task SignedOutCaller_SeesNothing()
    {
        await RequestAsync(Production, "Bar");

        (await EntriesAsync(userId: null, Production)).Should().BeEmpty();
        await _permissions.DidNotReceiveWithAnyArgs().GetOwnersAsync(default, default!, default);
    }

    [HumansFact]
    public async Task ThePersonWhoAsked_SeesNothing_ForTheirOwnRequest()
    {
        await RequestAsync(Production, "Bar");

        (await EntriesAsync(_sam)).Should().BeEmpty();
    }

    [HumansFact]
    public async Task NothingShows_OnceEveryRequestIsAnswered()
    {
        var task = await RequestAsync(Production, "Bar");
        (await EntriesAsync(_manager, Production)).Should().ContainSingle();

        await _kit.Service.DeclineRequestAsync(task, _sam, null, _manager, Ct);

        (await EntriesAsync(_manager, Production)).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Requests_OnDoneTask_StillCounted()
    {
        var done = await RequestAsync(Production, "Old");
        await _kit.SetStatusAsync(done, Humans.Tasks.Domain.TaskItemStatus.Done);

        (await EntriesAsync(_manager, Production)).Should().ContainSingle();
    }

    [HumansFact]
    public async Task RequestsOnAHiddenTask_ShowToItsManagers()
    {
        await RequestAsync(Production, "Quiet one", hidden: true);

        (await EntriesAsync(_manager, Production)).Should().ContainSingle();
    }

    [HumansFact]
    public async Task ConfirmedPeople_AreNotCountedAsWaiting()
    {
        var task = await _kit.CreateAsync(Input(team: Production, slots: 2));
        await _kit.Service.ClaimAsync(task.Id, _sam, Ct);

        (await EntriesAsync(_manager, Production)).Should().BeEmpty();
    }
}
