using AwesomeAssertions;
using Humans.Base.Enums;
using Humans.Camps.ViewComponents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Humans.Camps.Tests.ViewComponents;

public sealed class MyCampsViewComponentTests
{
    [HumansFact]
    public async Task InvokeAsync_RequestAborted_PropagatesCancellation()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var camps = Substitute.For<ICampService>();
        camps.GetSettingsAsync(aborted.Token)
            .Returns(Task.FromException<CampSettingsInfo>(new OperationCanceledException(aborted.Token)));
        var component = Build(camps, new DefaultHttpContext { RequestAborted = aborted.Token });

        var act = () => component.InvokeAsync(Guid.NewGuid(), ProfileCardViewMode.Self);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [HumansTheory]
    [Xunit.InlineData(ProfileCardViewMode.Public)]
    [Xunit.InlineData(ProfileCardViewMode.Admin)]
    public async Task InvokeAsync_NotOwnProfile_RendersNothing(ProfileCardViewMode viewMode)
    {
        var camps = Substitute.For<ICampService>();
        var component = Build(camps, new DefaultHttpContext());

        var result = await component.InvokeAsync(Guid.NewGuid(), viewMode);

        result.Should().BeOfType<ContentViewComponentResult>().Which.Content.Should().BeEmpty();
        camps.ReceivedCalls().Should().BeEmpty();
    }

    private static MyCampsViewComponent Build(ICampService camps, HttpContext httpContext) =>
        new(camps, NullLogger<MyCampsViewComponent>.Instance)
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext { HttpContext = httpContext }
            }
        };
}
