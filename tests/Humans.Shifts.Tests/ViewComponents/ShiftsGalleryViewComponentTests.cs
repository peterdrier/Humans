using AwesomeAssertions;
using Humans.Shifts.Contracts;
using Humans.Shifts.Services;
using Humans.Shifts.Services.Dtos;
using NodaTime;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
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
            Substitute.For<IShiftManagement>(), settings,
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
    [HumansFact]
    public async Task InvokeAsync_CancelsTheRotaReadWhenTheRequestIsAborted()
    {
        using var aborted = new CancellationTokenSource();
        var es = new BurnSettingsInfo(Guid.NewGuid(), "Event", 2026, "Europe/Madrid", new LocalDate(2026, 7, 9),
            -7, 4, 6, -25, -16, -9, -4, new Dictionary<int, int>(), null, null, true);
        var settings = Substitute.For<IBurnSettingsService>();
        settings.GetActiveAsync(Arg.Any<CancellationToken>()).Returns(es);
        var shifts = Substitute.For<IShiftManagement>();
        shifts.GetDepartmentsWithRotasAsync(es.Id, Arg.Any<CancellationToken>()).Returns([(Guid.NewGuid(), "Gate")]);
        shifts.GetRotasByDepartmentAsync(Arg.Any<Guid>(), es.Id, Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await aborted.CancelAsync();
                call.Arg<CancellationToken>().ThrowIfCancellationRequested();
                return (IReadOnlyList<ShiftAdminRota>)[];
            });
        var component = new ShiftsGalleryViewComponent(shifts, settings, NullLogger<ShiftsGalleryViewComponent>.Instance)
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext
                {
                    HttpContext = new DefaultHttpContext { RequestAborted = aborted.Token },
                    ViewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary())
                }
            }
        };

        var act = () => component.InvokeAsync();

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

}
