using System.Security.Claims;
using Microsoft.AspNetCore.Hosting;

namespace Humans.Base.Interfaces;

/// <summary>
/// One group in the admin sidebar: a single sidebar row whose <paramref name="Items"/> render as
/// the tab strip on each of its pages. <paramref name="Label"/> is the owning section's name and
/// the merge identity — a section that contributes into another's group ("Feedback" into
/// "Issues") names the same label.
/// </summary>
/// <remarks>Groups render alphabetically by label; there is no group weight.</remarks>
public sealed record AdminNavGroup(string Label, IReadOnlyList<AdminNavItem> Items);

/// <summary>One item in an admin sidebar group: a tab on the group's pages.</summary>
/// <remarks>
/// <paramref name="Label"/> sits under the group's label in the breadcrumb and the tab strip, so
/// it must not repeat it ("Catalog", not "Store catalog"). A single-item group whose item shares
/// its label renders that label once.
/// </remarks>
public sealed record AdminNavItem(
    string Label,
    string? Controller,
    string? Action,
    object? RouteValues,
    string? RawHref,
    string IconCssClass,
    string? Policy,
    Func<ClaimsPrincipal, bool>? RoleCheck = null,
    Func<IServiceProvider, ValueTask<int?>>? PillCount = null,
    Func<IWebHostEnvironment, bool>? EnvironmentGate = null,
    int Weight = 0);

/// <summary>The admin sidebar groups a section contributes.</summary>
public interface ISectionAdminNav : ISectionContribution
{
    IEnumerable<AdminNavGroup> Groups();
}
