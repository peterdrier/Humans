using AwesomeAssertions;
using Humans.Shifts.Contracts;
using Humans.Shifts.Services;
using Humans.Shifts.ViewComponents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Humans.Shifts.Tests.ViewComponents;

public sealed class ShiftsGalleryViewComponentTests
{
    [HumansFact]
    public async Task InvokeAsync_RequestAborted_PropagatesCancellation()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var settings = Substitute.For<IBurnSettingsService>();
        settings.GetActiveAsync(aborted.Token).Returns(Task.FromException<BurnSettingsInfo?>(
            new OperationCanceledException(aborted.Token)));
        var component = new ShiftsGalleryViewComponent(
            Substitute.For<IShiftManagementService>(), settings,
            NullLogger<ShiftsGalleryViewComponent>.Instance)
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext
                {
                    HttpContext = new DefaultHttpContext { RequestAborted = aborted.Token }
                }
            }
        };

        var act = () => component.InvokeAsync();

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
