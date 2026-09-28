using AwesomeAssertions;
using Humans.Onboarding.Services;
using Humans.Onboarding.ViewComponents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using System.Security.Claims;

namespace Humans.Onboarding.Tests.ViewComponents;

public sealed class OnboardingProgressBannerViewComponentTests
{
    [HumansFact]
    public async Task InvokeAsync_RequestAborted_PropagatesCancellation()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var state = Substitute.For<IOnboardingWidgetState>();
        state.GetCurrentStepAsync(Arg.Any<Guid>(), aborted.Token)
            .Returns(Task.FromException<OnboardingWidgetStep>(new OperationCanceledException(aborted.Token)));
        var httpContext = new DefaultHttpContext { RequestAborted = aborted.Token };
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "test"));
        var component = new OnboardingProgressBannerViewComponent(
            state, NullLogger<OnboardingProgressBannerViewComponent>.Instance)
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
