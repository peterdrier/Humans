using Humans.Application.Models;

namespace Humans.Application.Services.Agent;

/// <summary>Hard-coded per-1M-token Anthropic pricing for agent spend estimates. Unknown models fall back to sonnet-4-6.</summary>
public static class AgentPricing
{
    /// <summary>USD per 1,000,000 tokens.</summary>
    public sealed record PriceRow(decimal Input, decimal Output, decimal CacheRead);

    // Anthropic published rates (May 2026), per 1M tokens, input/output/cache-read. Cache-write (1.25× input) folded into input estimate.
    private static readonly Dictionary<string, PriceRow> _pricesByModelPrefix = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude-sonnet-4"] = new PriceRow(3.00m, 15.00m, 0.30m),
        ["claude-haiku-4"] = new PriceRow(0.80m, 4.00m, 0.08m),
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
        var input = (decimal)promptTokens / 1_000_000m * row.Input;
        var output = (decimal)outputTokens / 1_000_000m * row.Output;
        var cacheRead = (decimal)cachedTokens / 1_000_000m * row.CacheRead;
        return new AgentSpendStats(input, output, cacheRead, input + output + cacheRead);
    }
}
