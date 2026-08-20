namespace Humans.Base.Interfaces;

/// <summary>How prominently a tile renders.</summary>
public enum TileSeverity
{
    Normal,
    Warning,
    Critical
}

/// <summary>
/// One summary tile on the admin dashboard. The value delegate returns pre-formatted display
/// text, so no section type crosses the boundary.
/// </summary>
/// <remarks>
/// <paramref name="Label"/> is a resource key; a key with no entry renders as itself.
/// A null <paramref name="Value"/> result means "nothing to show" and the tile is skipped —
/// that is how a section with no data renders nothing rather than a zero.
/// </remarks>
public sealed record AdminTile(
    string Key,
    string Label,
    string IconCssClass,
    Func<IServiceProvider, CancellationToken, ValueTask<AdminTileValue?>> Value,
    string? Controller = null,
    string? Action = null,
    string? RawHref = null,
    string? Policy = null,
    int Weight = 0);

/// <summary>A tile's rendered value: the display text, an optional sub-line, and its severity.</summary>
/// <remarks>
/// <paramref name="Secondary"/> is the de-emphasised tail of the headline ("/ 240"), and
/// <paramref name="Summary"/> the section's own phrasing of the same number for the one-line
/// dashboard strapline ("240 with ticket"); a tile that says nothing there leaves it null.
/// </remarks>
public sealed record AdminTileValue(
    string Display,
    string? Detail = null,
    TileSeverity Severity = TileSeverity.Normal,
    string? Secondary = null,
    string? Summary = null);

/// <summary>The admin dashboard tiles a section contributes.</summary>
public interface ISectionAdminTiles : ISectionContribution
{
    IEnumerable<AdminTile> Tiles();
}
