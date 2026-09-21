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
/// The /Settings#agent tab body (peterdrier/Humans#1634) — the same mapping
/// AdminAgentController.Settings used to build before that GET became a redirect.
/// </summary>
public sealed class AgentSettingsTabViewComponentTests
{
    [HumansFact]
    public void Invoke_MapsCurrentSettingsOntoTheForm()
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

        var sut = new AgentSettingsTabViewComponent(settings);

        var result = sut.Invoke();
        var model = (AdminAgentSettingsViewModel)((ViewViewComponentResult)result).ViewData!.Model!;

        model.Enabled.Should().BeTrue();
        model.Model.Should().Be("claude-sonnet-4-6");
        model.PreloadConfig.Should().Be(AgentPreloadConfig.Tier2);
        model.DailyMessageCap.Should().Be(30);
        model.HourlyMessageCap.Should().Be(10);
        model.DailyTokenCap.Should().Be(50000);
        model.RetentionDays.Should().Be(90);
    }
}
