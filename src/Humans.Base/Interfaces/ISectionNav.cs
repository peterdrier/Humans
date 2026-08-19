using System.Security.Claims;

namespace Humans.Base.Interfaces;

/// <summary>
/// One member top-nav link. <paramref name="Label"/> is a SharedResource key; a key with no
/// entry renders as itself, which is how the links that were literal strings stay literal.
/// </summary>
/// <remarks>
/// <paramref name="Visible"/> carries the gating the layout used to branch on inline —
/// feature flags, authenticated/has-profile state — and is checked in addition to
/// <paramref name="Policy"/>. <paramref name="Children"/> renders the item as a dropdown;
/// children are gated by their own <paramref name="Policy"/>/<paramref name="Visible"/> the
/// same way top-level items are. A child with <paramref name="Divider"/> set renders a
/// dropdown-menu divider instead of a link, gated the same way as the group it introduces.
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
    bool Divider = false);

/// <summary>The member top-nav links a section contributes.</summary>
public interface ISectionNav : ISectionContribution
{
    IEnumerable<MemberNavItem> Items();
}
