using Humans.Base.Interfaces;
using Microsoft.AspNetCore.Html;

namespace Humans.Base.Models;

/// <summary>
/// One stat tile for <c>_StatTile</c>: a label, one big value, and an optional subline.
/// Wrap tiles in <c>&lt;div class="stats"&gt;</c> for the grid.
/// </summary>
/// <remarks>
/// <paramref name="Secondary"/> is the de-emphasised tail of the value ("/ 240").
/// <paramref name="Severity"/> colours the subline only, so it shows nothing without one.
/// <paramref name="Extra"/> is optional markup rendered under the subline (e.g. a badge row).
/// </remarks>
public sealed record StatTileViewModel(
    string Label,
    string Value,
    string? Secondary = null,
    string? Subline = null,
    TileSeverity Severity = TileSeverity.Normal,
    IHtmlContent? Extra = null);
