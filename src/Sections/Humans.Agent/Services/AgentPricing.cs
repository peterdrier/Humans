using Humans.Agent.Models;

namespace Humans.Agent.Services;

/// <summary>Hard-coded per-1M-token Anthropic pricing for agent spend estimates. Unknown models fall back to sonnet-4-6.</summary>
internal static class AgentPricing
{
    /// <summary>USD per 1,000,000 tokens.</summary>
    internal sealed record PriceRow(decimal Input, decimal Output, decimal CacheRead);

    // Anthropic published rates (https://platform.claude.com/docs/en/about-claude/pricing), per 1M tokens, input/output/cache-read. Cache-write (1.25× input) folded into input estimate.
    private static readonly Dictionary<string, PriceRow> _pricesByModelPrefix = new(StringComparer.OrdinalIgnoreCase)
    {
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

    public static PriceRow GetPriceRow(string model)
    {
        if (string.IsNullOrWhiteSpace(model)) return _fallback;
        foreach (var (prefix, row) in _pricesByModelPrefix)
        {
            if (model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return row;
        }
        return _fallback;
    }

    /// <summary>USD for one message. <paramref name="promptTokens"/> excludes cache-read (Anthropic reports separately). Slightly under-counts cache-warm phases.</summary>
    public static AgentSpendStats Compute(long promptTokens, long outputTokens, long cachedTokens, string model)
    {
        var row = GetPriceRow(model);
        var input = promptTokens / 1_000_000m * row.Input;
        var output = outputTokens / 1_000_000m * row.Output;
        var cacheRead = cachedTokens / 1_000_000m * row.CacheRead;
        return new AgentSpendStats(input, output, cacheRead, input + output + cacheRead);
    }
}
