namespace Humans.Web.Models;

/// <summary>
/// One drawing in the logo library: wwwroot/img/logo-library/&lt;Slug&gt;/entry.json beside its logo.svg,
/// written by the /redraw-logo skill. Effort and Brief are null for rounds drawn before they were recorded.
/// Notes carry every supported culture inside the entry, so parallel entries never share a resource file.
/// </summary>
public sealed record LogoLibraryEntry
{
    public string Slug { get; init; } = string.Empty;
    public string Date { get; init; } = string.Empty;
    public string Family { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string? Effort { get; init; }
    public string? Brief { get; init; }
    public IReadOnlyDictionary<string, string> Notes { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
    public LogoRunStats? Stats { get; init; }

    public string NotesIn(string cultureCode) =>
        Notes.GetValueOrDefault(cultureCode) ?? Notes.GetValueOrDefault("en") ?? string.Empty;
}

/// <summary>
/// What the drawing run cost, from stats.json beside entry.json (written by the /redraw-logo skill's stats.py).
/// Any part can be null: older runs have no record, and unpriced models have no cost.
/// </summary>
public sealed record LogoRunStats
{
    public double? Minutes { get; init; }
    public long? InputTokens { get; init; }
    public long? OutputTokens { get; init; }
    public long? CacheReadTokens { get; init; }
    public long? CacheWriteTokens { get; init; }
    public decimal? CostUsd { get; init; }

    /// <summary>Total tokens, or null when no token counts were recorded.</summary>
    public long? TotalTokens => InputTokens is null && OutputTokens is null && CacheReadTokens is null && CacheWriteTokens is null
        ? null
        : (InputTokens ?? 0) + (OutputTokens ?? 0) + (CacheReadTokens ?? 0) + (CacheWriteTokens ?? 0);
}
