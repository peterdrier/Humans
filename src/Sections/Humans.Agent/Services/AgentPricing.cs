using Humans.Agent.Models;

namespace Humans.Agent.Services;

/// <summary>Per-1M-token rates for spend estimates. Unknown models fall back to sonnet-4-6 rates.</summary>
internal static class AgentPricing
{
    internal sealed record PriceRow(decimal Input, decimal Output, decimal CacheRead);

    // https://platform.claude.com/docs/en/about-claude/pricing, as of October 2026. Anthropic publishes no pricing API,
    // so update this table when new models ship. Cache writes are priced at the input rate, without their premium.
    private static readonly Dictionary<string, PriceRow> _pricesByModelPrefix = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude-fable-5"] = new PriceRow(10.00m, 50.00m, 1.00m),
        ["claude-fable-5-1"] = new PriceRow(10.00m, 50.00m, 0.25m),
        ["claude-mythos-5"] = new PriceRow(10.00m, 50.00m, 1.00m),
        ["claude-mythos-5-1"] = new PriceRow(10.00m, 50.00m, 0.25m),
        ["claude-opus-5"] = new PriceRow(5.00m, 25.00m, 0.50m),
        ["claude-opus-5-5"] = new PriceRow(4.00m, 20.00m, 0.20m),
        ["claude-sonnet-5"] = new PriceRow(2.00m, 10.00m, 0.20m),
        ["claude-sonnet-5-5"] = new PriceRow(2.00m, 10.00m, 0.10m),
        // Up to 100K prompt tokens; longer prompts bill at $0.50 / $2.50.
        ["claude-haiku-5-5"] = new PriceRow(0.10m, 0.50m, 0.01m),
        ["claude-sonnet-4"] = new PriceRow(3.00m, 15.00m, 0.30m),
        ["claude-haiku-4"] = new PriceRow(1.00m, 5.00m, 0.10m),
        // Match newer Opus versions before the legacy Opus 4 prefix.
        ["claude-opus-4-5"] = new PriceRow(5.00m, 25.00m, 0.50m),
        ["claude-opus-4-6"] = new PriceRow(5.00m, 25.00m, 0.50m),
        ["claude-opus-4-7"] = new PriceRow(5.00m, 25.00m, 0.50m),
        ["claude-opus-4-8"] = new PriceRow(5.00m, 25.00m, 0.50m),
        ["claude-opus-4"] = new PriceRow(15.00m, 75.00m, 1.50m),
    };

    private static readonly PriceRow _fallback = new(3.00m, 15.00m, 0.30m);

    public static PriceRow GetPriceRow(string model) => FindPriceRow(model) ?? _fallback;

    /// <summary>Null for a model the table doesn't know, where <see cref="GetPriceRow"/> would guess Sonnet rates.</summary>
    public static PriceRow? FindPriceRow(string model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        // Longest matching prefix wins, so claude-opus-4-5 never falls through to claude-opus-4.
        return _pricesByModelPrefix
            .Where(p => model.StartsWith(p.Key, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.Key.Length)
            .Select(p => p.Value)
            .FirstOrDefault();
    }

    /// <summary><paramref name="promptTokens"/> excludes cache reads, which Anthropic reports separately.</summary>
    public static AgentSpendStats Compute(long promptTokens, long outputTokens, long cachedTokens, string model)
    {
        var row = GetPriceRow(model);
        var input = promptTokens / 1_000_000m * row.Input;
        var output = outputTokens / 1_000_000m * row.Output;
        var cacheRead = cachedTokens / 1_000_000m * row.CacheRead;
        return new AgentSpendStats(input, output, cacheRead, input + output + cacheRead);
    }
}
