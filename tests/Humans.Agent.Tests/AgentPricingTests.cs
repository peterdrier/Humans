using AwesomeAssertions;
using Xunit;
using Humans.Agent.Services;
using Humans.Agent.Contracts;

namespace Humans.Agent.Tests;

public class AgentPricingTests
{
    [HumansTheory]
    [InlineData("claude-sonnet-4-6", 3, 15, 0.30)]
    [InlineData("claude-haiku-4-5-20251001", 1, 5, 0.10)]
    [InlineData("claude-opus-4-5-20251101", 5, 25, 0.50)]
    [InlineData("CLAUDE-OPUS-4-6", 5, 25, 0.50)]
    [InlineData("claude-opus-4-7", 5, 25, 0.50)]
    [InlineData("claude-opus-4-8", 5, 25, 0.50)]
    [InlineData("claude-opus-4-1-20250805", 15, 75, 1.50)]
    [InlineData("claude-opus-4-20250514", 15, 75, 1.50)]
    public void Model_prefix_resolves_to_published_rates(
        string model, decimal input, decimal output, decimal cacheRead)
    {
        var row = AgentPricing.GetPriceRow(model);
        row.Input.Should().Be(input);
        row.Output.Should().Be(output);
        row.CacheRead.Should().Be(cacheRead);
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
