using System.Security.Claims;

namespace Humans.Base.Interfaces;

/// <summary>
/// One entry in the member's things-to-do list. <paramref name="Text"/> and the display
/// members below are already localized — a section resolves its own copy, so its keys can
/// move into its own resource set without Shell knowing.
/// </summary>
public sealed record ThingsToDoEntry(
    string Key,
    string Text,
    string IconCssClass,
    string? Controller = null,
    string? Action = null,
    string? RawHref = null,
    int Weight = 0,
    TileSeverity Severity = TileSeverity.Normal)
{
    /// <summary>The second line, under the title.</summary>
    public string? Description { get; init; }

    /// <summary>A done entry renders struck through and without its action.</summary>
    public bool IsDone { get; init; }

    /// <summary>Label on the action button; no label, no button.</summary>
    public string? ActionText { get; init; }

    /// <summary>0–100 progress bar for a graded entry; null draws no bar.</summary>
    public int? PercentComplete { get; init; }
}

/// <summary>
/// The things-to-do entries a section contributes for the signed-in member. Returning
/// nothing is the normal case — an entry appears only when that section wants action.
/// </summary>
public interface ISectionThingsToDo : ISectionContribution
{
    ValueTask<IEnumerable<ThingsToDoEntry>> EntriesAsync(IServiceProvider services, ClaimsPrincipal user);
}
