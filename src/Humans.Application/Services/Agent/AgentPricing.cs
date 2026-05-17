using Humans.Application.Models;

namespace Humans.Application.Services.Agent;

/// <summary>
/// Hard-coded USD pricing for the models we point the agent at. Per-1M-token
/// rates as published by Anthropic for the models we support. Used to surface
/// spend on the admin status view — the agent runs one model at a time so a
/// table-driven lookup is sufficient. If a turn used an unknown model, the
/// lookup falls back to the configured default (claude-sonnet-4-6) — the
/// admin panel labels this as "estimate" anyway.
/// </summary>
public static class AgentPricing
{
    /// <summary>USD per 1,000,000 tokens.</summary>
    public sealed record PriceRow(decimal Input, decimal Output, decimal CacheRead);

    // Anthropic published rates (per Anthropic pricing page, May 2026):
    //   claude-sonnet-4-6:   $3.00 / $15.00 / $0.30 (input / output / cache-read)
    //   claude-haiku-4:      $0.80 / $4.00  / $0.08
    //   claude-opus-4:       $15.00 / $75.00 / $1.50
    // Cache-write would be 1.25× input but we don't break it out — most cache
    // entries are reused, so the spend view shows the input+output+cache-read
    // breakdown that maps to the columns we record on AgentMessage.
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

    /// <summary>
    /// Compute USD for a single message's token counts. <paramref name="promptTokens"/>
    /// already excludes the cache-read portion (Anthropic reports them separately
    /// — see <c>AnthropicClient</c> usage capture). Cache-write tokens are not
    /// tracked separately on <c>AgentMessage</c>; they fold into the input total
    /// at the standard input rate, which slightly under-counts spend in heavy
    /// cache-warm phases — fine for an estimate, called out in the view.
    /// </summary>
    public static AgentSpendStats Compute(long promptTokens, long outputTokens, long cachedTokens, string model)
    {
        var row = GetPriceRow(model);
        var input = (decimal)promptTokens / 1_000_000m * row.Input;
        var output = (decimal)outputTokens / 1_000_000m * row.Output;
        var cacheRead = (decimal)cachedTokens / 1_000_000m * row.CacheRead;
        return new AgentSpendStats(input, output, cacheRead, input + output + cacheRead);
    }
}
