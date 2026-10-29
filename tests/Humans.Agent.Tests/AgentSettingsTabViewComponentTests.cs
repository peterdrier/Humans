using AwesomeAssertions;
using Humans.Agent.Domain;
using Humans.Agent.Models;
using Humans.Agent.Services;
using Humans.Agent.ViewComponents;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using NodaTime;
using NSubstitute;

namespace Humans.Agent.Tests;

/// <summary>
/// The /Settings#agent tab body maps current settings onto the form.
/// </summary>
public sealed class AgentSettingsTabViewComponentTests
{
    [HumansFact]
    public async Task Invoke_MapsCurrentSettingsOntoTheForm()
    {
        var settings = Substitute.For<IAgentSettingsService>();
        settings.Current.Returns(new AgentSettingsDto(
            Enabled: true,
            Model: "claude-sonnet-4-6",
            PreloadConfig: AgentPreloadConfig.Tier2,
            DailyMessageCap: 30,
            HourlyMessageCap: 10,
            DailyTokenCap: 50000,
            RetentionDays: 90,
            UpdatedAt: Instant.FromUtc(2026, 4, 21, 0, 0)));

        var modelOptions = new AgentModelOptions([new AgentModelOption("claude-sonnet-4-6", "Claude Sonnet 4.6", null)], true);
        settings.GetModelOptionsAsync(Arg.Any<CancellationToken>()).Returns(modelOptions);
        var sut = new AgentSettingsTabViewComponent(settings)
        {
            ViewComponentContext = new ViewComponentContext { ViewContext = new Microsoft.AspNetCore.Mvc.Rendering.ViewContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() } }
        };

        var result = await sut.InvokeAsync();
        var model = (AdminAgentSettingsViewModel)((ViewViewComponentResult)result).ViewData!.Model!;

        model.Enabled.Should().BeTrue();
        model.Model.Should().Be("claude-sonnet-4-6");
        model.PreloadConfig.Should().Be(AgentPreloadConfig.Tier2);
        model.DailyMessageCap.Should().Be(30);
        model.HourlyMessageCap.Should().Be(10);
        model.DailyTokenCap.Should().Be(50000);
        model.RetentionDays.Should().Be(90);
        model.ModelOptions.Should().BeSameAs(modelOptions);
    }
}
