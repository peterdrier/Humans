using AwesomeAssertions;
using Humans.Issues.Contracts;
using Humans.Issues.Services;
using Humans.Issues.ViewComponents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using NSubstitute;
using System.Security.Claims;

namespace Humans.Issues.Tests.ViewComponents;

public sealed class IssuesUserMenuViewComponentTests
{
    [HumansFact]
    public async Task InvokeAsync_RequestAborted_PropagatesCancellation()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var issues = Substitute.For<IIssuesService>();
        issues.GetActionableCountForViewerAsync(Arg.Any<IssueViewer>(), aborted.Token)
            .Returns(Task.FromException<int>(new OperationCanceledException(aborted.Token)));
        var httpContext = new DefaultHttpContext { RequestAborted = aborted.Token };
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "test"));
        var component = new IssuesUserMenuViewComponent(issues)
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
