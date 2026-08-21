using System.Security.Claims;
using Microsoft.AspNetCore.Hosting;

namespace Humans.Base.Interfaces;

/// <summary>
/// One group in the admin sidebar. <paramref name="Key"/> is the merge identity — several
/// sections contribute into one group ("Tickets" holds Tickets+Campaigns+Scanner+Gate+
/// EarlyEntry; "Money" holds Expenses+Budget+Holded+Store) — and defaults to
/// <paramref name="Label"/> when not given.
/// </summary>
/// <remarks>
/// <paramref name="Weight"/> orders groups; <c>System: true</c> groups are AdminOnly plumbing
/// rendered below a divider. Order is by daily traffic across the whole admin audience, NOT
/// structural prominence — weights carry that editorial judgement, so do not re-sort.
/// </remarks>
public sealed record AdminNavGroup(
    string Label,
    IReadOnlyList<AdminNavItem> Items,
    bool System = false,
    string? Key = null,
    int Weight = 0)
{
    /// <summary>Merge identity — <see cref="Key"/>, or <see cref="Label"/> when unset.</summary>
    public string GroupKey => Key ?? Label;
}

/// <summary>One item in an admin sidebar group.</summary>
/// <remarks>
/// <paramref name="Label"/> must stand alone in the sidebar, where the heading above it is the
/// merge group ("Money"), not the owning section. The breadcrumb states the section, so a label
/// that repeats it reads twice; <paramref name="BreadcrumbLabel"/> is the shorter form to use
/// there. Leave it unset when the label is already section-free.
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
    int Weight = 0,
    string? BreadcrumbLabel = null)
{
    /// <summary>What the breadcrumb renders — <see cref="BreadcrumbLabel"/>, or <see cref="Label"/>.</summary>
    public string CrumbLabel => BreadcrumbLabel ?? Label;
}

/// <summary>The admin sidebar groups a section contributes.</summary>
public interface ISectionAdminNav : ISectionContribution
{
    IEnumerable<AdminNavGroup> Groups();
}
