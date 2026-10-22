using System.Security.Claims;

namespace Humans.Base.Interfaces;

/// <summary>
/// One member top-nav link. <paramref name="Label"/> is a SharedResource key; a key with no entry renders as itself.
/// </summary>
/// <remarks>
/// <paramref name="Visible"/> carries extra gating (feature flags, authenticated/has-profile state), checked in
/// addition to <paramref name="Policy"/>. <paramref name="Children"/> renders the item as a dropdown, each child gated
/// the same way; a child with <paramref name="Divider"/> set renders a divider, gated like the group it introduces.
/// <paramref name="IconCssClass"/> renders an icon instead of the label on a top-level item, with
/// <paramref name="Label"/> still the accessible name (<c>aria-label</c>/<c>title</c>).
/// </remarks>
public sealed record MemberNavItem(
    string Label,
    string? Controller = null,
    string? Action = null,
    string? RawHref = null,
    string? Policy = null,
    int Weight = 0,
    string? CssClass = null,
    Func<IServiceProvider, ClaimsPrincipal, bool>? Visible = null,
    IReadOnlyList<MemberNavItem>? Children = null,
    bool Divider = false,
    string? IconCssClass = null);

/// <summary>The member top-nav links a section contributes.</summary>
public interface ISectionNav : ISectionContribution
{
    IEnumerable<MemberNavItem> Items();
}
