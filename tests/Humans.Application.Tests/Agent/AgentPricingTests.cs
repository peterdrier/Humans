using AwesomeAssertions;
using Humans.Application.Services.Agent;

namespace Humans.Application.Tests.Agent;

public class AgentPricingTests
{
    [HumansFact]
    public void Sonnet_prefix_resolves_to_sonnet_rates()
    {
        var row = AgentPricing.GetPriceRow("claude-sonnet-4-6");
        row.Input.Should().Be(3.00m);
        row.Output.Should().Be(15.00m);
        row.CacheRead.Should().Be(0.30m);
    }

    [HumansFact]
    public void Unknown_model_falls_back_to_default_rates()
    {
        var row = AgentPricing.GetPriceRow("some-future-model");
        row.Input.Should().Be(3.00m);
    }

    [HumansFact]
    public void Compute_scales_by_million_tokens()
    {
        // 1,000,000 input tokens at $3 → $3.00, 500,000 output at $15 → $7.50,
        // 100,000 cache-read at $0.30 → $0.03.
        var spend = AgentPricing.Compute(1_000_000, 500_000, 100_000, "claude-sonnet-4-6");
        spend.InputUsd.Should().Be(3.00m);
        spend.OutputUsd.Should().Be(7.50m);
        spend.CacheReadUsd.Should().Be(0.03m);
        spend.TotalUsd.Should().Be(10.53m);
    }
}
