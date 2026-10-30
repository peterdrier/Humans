using AwesomeAssertions;
using Humans.Camps.ViewComponents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using NSubstitute;

namespace Humans.Camps.Tests;

public sealed class CampBarriosSettingsTabViewComponentTests
{
    [HumansFact]
    public async Task InvokeAsync_ReturnsTheRegistrationStateOfEveryEvent()
    {
        var campService = Substitute.For<ICampManagement>();
        IReadOnlyList<CampEventRegistration> events =
        [
            new(Guid.NewGuid(), "Nowhere 2026", 2026, RegistrationOpen: true),
            new(Guid.NewGuid(), "Nowhere 2025", 2025, RegistrationOpen: false),
        ];
        campService.GetRegistrationByEventAsync(Arg.Any<CancellationToken>()).Returns(events);

        var sut = new CampBarriosSettingsTabViewComponent(campService)
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext { HttpContext = new DefaultHttpContext() },
            },
        };
        var result = await sut.InvokeAsync();
        var model = (CampBarriosSettingsViewModel)((ViewViewComponentResult)result).ViewData!.Model!;

        model.Events.Should().Equal(events);
    }
}
