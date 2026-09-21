using AwesomeAssertions;
using Humans.CityPlanning.Contracts;
using Humans.CityPlanning.ViewComponents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using NodaTime;
using NSubstitute;

namespace Humans.CityPlanning.Tests;

/// <summary>The /Settings#city-planning tab (peterdrier/Humans#1634).</summary>
public sealed class CityPlanningSettingsTabViewComponentTests
{
    [HumansFact]
    public async Task InvokeAsync_ReturnsSettingsAndRegistrationInfo()
    {
        var service = Substitute.For<ICityPlanningServiceRead>();
        var settings = new CityPlanningSettingsDto(
            Id: Guid.NewGuid(), Year: 2026, IsPlacementOpen: true, OpenedAt: null, ClosedAt: null,
            PlacementOpensAt: null, PlacementClosesAt: null, RegistrationInfo: "unused-year-scoped-field",
            LimitZoneGeoJson: null, OfficialZonesGeoJson: null, IsContainerPlacementOpen: false,
            ContainerPlacementOpenedAt: null, ContainerPlacementClosedAt: null,
            UpdatedAt: Instant.FromUtc(2026, 1, 1, 0, 0));
        service.GetSettingsAsync(Arg.Any<CancellationToken>()).Returns(settings);
        // GetRegistrationInfoAsync is keyed to the highest open season, not settings.Year —
        // the tab must read it separately, not off CityPlanningSettingsDto.RegistrationInfo.
        service.GetRegistrationInfoAsync(Arg.Any<CancellationToken>()).Returns("Bring water.");

        var sut = new CityPlanningSettingsTabViewComponent(service)
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext { HttpContext = new DefaultHttpContext() },
            },
        };
        var result = await sut.InvokeAsync();
        var model = (CityPlanningSettingsTabViewModel)((ViewViewComponentResult)result).ViewData!.Model!;

        model.Settings.Year.Should().Be(2026);
        model.RegistrationInfo.Should().Be("Bring water.");
    }
}
