using AwesomeAssertions;
using Humans.Base.Interfaces;
using Humans.Users.Contracts;
using Humans.Workgroups.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Humans.Workgroups.Services;
using Humans.Workgroups.ViewComponents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using NodaTime;
using NSubstitute;
using System.Security.Claims;

namespace Humans.Workgroups.Tests.ViewComponents;

public sealed class MyWorkgroupsViewComponentTests
{
    public enum MemberSurface
    {
        Dashboard,
        Tasks
    }

    [HumansTheory]
    [Xunit.InlineData(MemberSurface.Dashboard, AccountScenario.Merged)]
    [Xunit.InlineData(MemberSurface.Dashboard, AccountScenario.Missing)]
    [Xunit.InlineData(MemberSurface.Dashboard, AccountScenario.Cancelled)]
    [Xunit.InlineData(MemberSurface.Tasks, AccountScenario.Merged)]
    [Xunit.InlineData(MemberSurface.Tasks, AccountScenario.Missing)]
    [Xunit.InlineData(MemberSurface.Tasks, AccountScenario.Cancelled)]
    public async Task MemberSurfaces_ResolveTheAccountBeforeLoadingWorkgroups(MemberSurface surface, AccountScenario account)
    {
        using var request = new CancellationTokenSource();
        var cookieId = Guid.NewGuid();
        var survivorId = Guid.NewGuid();
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(cookieId, Arg.Any<CancellationToken>()).Returns(call =>
        {
            call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            return new ValueTask<UserInfo?>(account == AccountScenario.Missing
                ? null : UserInfo.Create(new User { Id = survivorId }, [], [], null));
        });
        var joined = Instant.FromUtc(2026, 1, 1, 0, 0);
        var group = new WorkgroupInfo(Guid.NewGuid(), "group", "Group", "Purpose", "Report",
            WorkgroupDeliverableKind.Report, WorkgroupAudience.Board, null, WorkgroupStatus.Active,
            null, null, null, null, null, joined, joined, null,
            [new WorkgroupMemberInfo(Guid.NewGuid(), survivorId, WorkgroupMemberRole.Coordinator, joined, null),
                new WorkgroupMemberInfo(Guid.NewGuid(), Guid.NewGuid(), WorkgroupMemberRole.Member, joined, null, true)],
            [], [], [], null, null, null);
        var workgroups = Substitute.For<IWorkgroupService>();
        workgroups.GetForMemberAsync(survivorId, request.Token).Returns(new[] { group });
        var localizer = Substitute.For<IStringLocalizer<WorkgroupsResource>>();
        localizer[Arg.Any<string>()].Returns(call => new LocalizedString(call.Arg<string>(), call.Arg<string>()));
        using var services = new ServiceCollection().AddSingleton(users).AddSingleton(workgroups)
            .AddSingleton<IClock>(SystemClock.Instance).AddSingleton(localizer).BuildServiceProvider();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, cookieId.ToString())], "test"));
        async Task ReadAsync()
        {
            if (surface == MemberSurface.Tasks)
            {
                var tasks = (await ((ISectionThingsToDo)new Section()).EntriesAsync(services, principal, request.Token)).ToList();
                if (account == AccountScenario.Merged)
                    tasks.Should().Contain(entry => entry.Key == $"workgroup-requests-{group.Id}");
                else tasks.Should().BeEmpty();
            }
            else
            {
                var component = ActivatorUtilities.CreateInstance<MyWorkgroupsViewComponent>(services);
                component.ViewComponentContext = new ViewComponentContext
                {
                    ViewContext = new ViewContext { HttpContext = new DefaultHttpContext { User = principal, RequestAborted = request.Token } }
                };
                var result = await component.InvokeAsync();
                if (account == AccountScenario.Merged)
                {
                    var model = result.Should().BeOfType<ViewViewComponentResult>().Subject.ViewData!.Model
                        .Should().BeOfType<MyWorkgroupsViewModel>().Subject;
                    model.Mine.Should().ContainSingle().Which.Id.Should().Be(group.Id);
                }
                else result.Should().BeOfType<ContentViewComponentResult>().Which.Content.Should().BeEmpty();
            }
        }
        if (account == AccountScenario.Cancelled)
        {
            await request.CancelAsync();
            Func<Task> run = ReadAsync;
            await run.Should().ThrowAsync<OperationCanceledException>();
        }
        else await ReadAsync();
        if (account != AccountScenario.Merged) workgroups.ReceivedCalls().Should().BeEmpty();
    }

    [HumansFact]
    public async Task InvokeAsync_RequestAborted_PropagatesCancellation()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var workgroups = Substitute.For<IWorkgroupService>();
        workgroups.GetForMemberAsync(Arg.Any<Guid>(), aborted.Token)
            .Returns(Task.FromException<IReadOnlyList<WorkgroupInfo>>(
                new OperationCanceledException(aborted.Token)));
        var httpContext = new DefaultHttpContext { RequestAborted = aborted.Token };
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "test"));
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => UserInfo.Create(new User { Id = call.Arg<Guid>() }, [], [], null));
        var component = new MyWorkgroupsViewComponent(workgroups, SystemClock.Instance, users)
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext { HttpContext = httpContext }
            }
        };

        var act = () => component.InvokeAsync();

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
