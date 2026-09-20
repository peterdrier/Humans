using AwesomeAssertions;
using Humans.Gate.Models;
using Humans.Gate.Services;
using Humans.Gate.ViewComponents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using NodaTime;
using NSubstitute;

namespace Humans.Gate.Tests;

/// <summary>The /Settings#gate tab (peterdrier/Humans#1634).</summary>
public sealed class GateSettingsTabViewComponentTests
{
    [HumansFact]
    public async Task InvokeAsync_ReturnsTheCurrentSettings()
    {
        var gate = Substitute.For<IGateService>();
        var opensAt = Instant.FromUtc(2026, 7, 6, 10, 0);
        gate.GetSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(new GateSettingsDto(opensAt, 16));

        var sut = new GateSettingsTabViewComponent(gate)
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext { HttpContext = new DefaultHttpContext() },
            },
        };
        var result = await sut.InvokeAsync();
        var model = (GateSettingsViewModel)((ViewViewComponentResult)result).ViewData!.Model!;

        model.MinorAgeThresholdYears.Should().Be(16);
        model.GeneralEntryOpensAtUtc.Should().Be("2026-07-06T10:00:00Z");
    }
}
