using System.Security.Claims;

namespace Humans.Base.Interfaces;

/// <summary>
/// One entry in the member's things-to-do list. <paramref name="Text"/> is a resource key;
/// a key with no entry renders as itself.
/// </summary>
public sealed record ThingsToDoEntry(
    string Key,
    string Text,
    string IconCssClass,
    string? Controller = null,
    string? Action = null,
    string? RawHref = null,
    int Weight = 0,
    TileSeverity Severity = TileSeverity.Normal);

/// <summary>
/// The things-to-do entries a section contributes for the signed-in member. Returning
/// nothing is the normal case — an entry appears only when that section wants action.
/// </summary>
public interface ISectionThingsToDo : ISectionContribution
{
    ValueTask<IEnumerable<ThingsToDoEntry>> EntriesAsync(IServiceProvider services, ClaimsPrincipal user);
}
