using AwesomeAssertions;
using Humans.Base.ViewComponents;
using Humans.Base.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Claims;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using NSubstitute;
using Microsoft.Extensions.Localization;

namespace Humans.Base.Tests.ViewComponents;

public class HumanReadCancellationTests
{
    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task AbandonedRequest_CancelsHumanLookup(bool picker)
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var userId = Guid.NewGuid();
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(call =>
        {
            var token = call.Arg<CancellationToken>();
            return token.IsCancellationRequested
                ? ValueTask.FromCanceled<UserInfo?>(token)
                : ValueTask.FromResult<UserInfo?>(null);
        });
        ViewComponent component = picker
            ? new HumanSearchViewComponent(users)
            : new HumanViewComponent(users, Substitute.For<IUrlHelperFactory>(), Substitute.For<IStringLocalizer<SharedResource>>());
        component.ViewComponentContext = new ViewComponentContext
        {
            ViewContext = new ViewContext
            {
                HttpContext = new DefaultHttpContext { RequestAborted = aborted.Token },
            },
        };
        Func<Task> act = () => picker
            ? ((HumanSearchViewComponent)component).InvokeAsync(selectedUserId: userId)
            : ((HumanViewComponent)component).InvokeAsync(userId);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task ThingsToDo_PropagatesCancellationButIsolatesOperationalFailures(bool canceled)
    {
        using var aborted = new CancellationTokenSource();
        if (canceled) await aborted.CancelAsync();
        var contributor = Substitute.For<ISectionThingsToDo>();
        contributor.EntriesAsync(Arg.Any<IServiceProvider>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromException<IEnumerable<ThingsToDoEntry>>(
                canceled ? new OperationCanceledException(aborted.Token) : new IOException("Contributor unavailable")));
        var healthy = Substitute.For<ISectionThingsToDo>();
        healthy.EntriesAsync(Arg.Any<IServiceProvider>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(new[] { new ThingsToDoEntry("pending", "Pending", "icon", RawHref: "/") });
        var component = new ThingsToDoViewComponent([contributor, healthy], Substitute.For<IServiceProvider>(),
            NullLogger<ThingsToDoViewComponent>.Instance)
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext { HttpContext = new DefaultHttpContext { RequestAborted = aborted.Token } },
            },
        };
        var load = () => component.InvokeAsync();

        if (canceled)
            await load.Should().ThrowAsync<OperationCanceledException>();
        else
            (await load()).Should().BeOfType<ViewViewComponentResult>();
        await contributor.Received(1).EntriesAsync(Arg.Any<IServiceProvider>(), Arg.Any<ClaimsPrincipal>(), aborted.Token);
    }

    [HumansFact]
    public async Task ThingsToDo_IsolatesCancellationNotCausedByTheRequest()
    {
        var contributor = Substitute.For<ISectionThingsToDo>();
        contributor.EntriesAsync(Arg.Any<IServiceProvider>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromException<IEnumerable<ThingsToDoEntry>>(new TaskCanceledException("Timed out")));
        var healthy = Substitute.For<ISectionThingsToDo>();
        healthy.EntriesAsync(Arg.Any<IServiceProvider>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(new[] { new ThingsToDoEntry("pending", "Pending", "icon", RawHref: "/") });
        var component = new ThingsToDoViewComponent([contributor, healthy], Substitute.For<IServiceProvider>(),
            NullLogger<ThingsToDoViewComponent>.Instance)
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext { HttpContext = new DefaultHttpContext() },
            },
        };

        (await component.InvokeAsync()).Should().BeOfType<ViewViewComponentResult>();
    }

}
