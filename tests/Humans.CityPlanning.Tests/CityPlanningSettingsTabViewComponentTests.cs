using AwesomeAssertions;
using Humans.Camps.Contracts;
using Humans.CityPlanning.Contracts;
using Humans.CityPlanning.ViewComponents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.CityPlanning.Tests;

/// <summary>The /Settings#city-planning tab (peterdrier/Humans#1634).</summary>
public sealed class CityPlanningSettingsTabViewComponentTests
{
    [HumansFact]
    public async Task InvokeAsync_ReturnsSettingsAndRegistrationInfo()
    {
        var service = Substitute.For<ICityPlanningService>();
        var settings = new CityPlanningSettingsDto(
            Id: Guid.NewGuid(), Year: 2026, PlacementState: BarrioPlacementState.Open, PlacementOverride: true,
            OpenedAt: null, ClosedAt: null,
            PlacementOpensOffset: null, PlacementClosesOffset: null, PlacementChangeableUntilOffset: null,
            PlacementOpensOn: null, PlacementClosesOn: null, PlacementChangeableUntilOn: null,
            RegistrationInfo: "unused-year-scoped-field",
            LimitZoneGeoJson: null, OfficialZonesGeoJson: null, IsContainerPlacementOpen: false,
            ContainerPlacementOpenedAt: null, ContainerPlacementClosedAt: null,
            UpdatedAt: Instant.FromUtc(2026, 1, 1, 0, 0));
        service.GetSettingsAsync(Arg.Any<CancellationToken>()).Returns(settings);
        // GetRegistrationInfoAsync is keyed to the highest open season, not settings.Year —
        // the tab must read it separately, not off CityPlanningSettingsDto.RegistrationInfo.
        service.GetRegistrationInfoAsync(Arg.Any<CancellationToken>()).Returns("Bring water.");

        var model = await InvokeAsync(service, openSeasons: []);

        model.Settings!.Year.Should().Be(2026);
        model.RegistrationInfo.Should().Be("Bring water.");
        model.RegistrationInfoEditable.Should().BeTrue();
    }

    /// <summary>
    /// With no active event the placement cards go, but registration info is keyed to the highest
    /// open season — the Register page can still show it, so the tab must still edit it.
    /// </summary>
    [HumansTheory]
    [InlineData(new[] { 2027 }, true)]
    [InlineData(new int[0], false)]
    public async Task InvokeAsync_NoActiveEvent_RegistrationInfoEditableOnlyWithAnOpenSeason(
        int[] openSeasons, bool editable)
    {
        var service = Substitute.For<ICityPlanningService>();
        service.GetSettingsAsync(Arg.Any<CancellationToken>()).Returns((CityPlanningSettingsDto?)null);
        service.GetRegistrationInfoAsync(Arg.Any<CancellationToken>()).Returns(editable ? "Bring water." : null);

        var model = await InvokeAsync(service, openSeasons);

        model.Settings.Should().BeNull();
        model.RegistrationInfoEditable.Should().Be(editable);
        model.RegistrationInfo.Should().Be(editable ? "Bring water." : null);
    }

    private static async Task<CityPlanningSettingsTabViewModel> InvokeAsync(
        ICityPlanningService service, IReadOnlyList<int> openSeasons)
    {
        var campService = Substitute.For<ICampService>();
        campService.GetSettingsAsync(Arg.Any<CancellationToken>()).Returns(new CampSettingsInfo(2026, openSeasons));
        var sut = new CityPlanningSettingsTabViewComponent(service, campService)
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext { HttpContext = new DefaultHttpContext() },
            },
        };
        var result = await sut.InvokeAsync();
        return (CityPlanningSettingsTabViewModel)((ViewViewComponentResult)result).ViewData!.Model!;
    }
}
