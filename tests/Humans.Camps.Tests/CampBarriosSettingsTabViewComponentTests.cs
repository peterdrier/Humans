using AwesomeAssertions;
using Humans.Camps.Contracts;
using Humans.Camps.Models;
using Humans.Camps.Services;
using Humans.Camps.ViewComponents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using NSubstitute;

namespace Humans.Camps.Tests;

/// <summary>The /Settings#barrios tab (peterdrier/Humans#1634).</summary>
public sealed class CampBarriosSettingsTabViewComponentTests
{
    [HumansFact]
    public async Task InvokeAsync_ReturnsTheOpenSeasons()
    {
        var campService = Substitute.For<ICampService>();
        campService.GetSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(new CampSettingsInfo(2026, [2025, 2026]));

        var sut = new CampBarriosSettingsTabViewComponent(campService)
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext { HttpContext = new DefaultHttpContext() },
            },
        };
        var result = await sut.InvokeAsync();
        var model = (CampBarriosSettingsViewModel)((ViewViewComponentResult)result).ViewData!.Model!;

        model.OpenSeasons.Should().Equal(2025, 2026);
    }
}
