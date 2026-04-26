using System.Security.Claims;
using Humans.Web.Authorization;

namespace Humans.Web.ViewComponents;

/// <summary>
/// A group of related admin sidebar items, rendered under an italic gold-tinted h4
/// divider. Groups whose items are all hidden by authorization disappear entirely.
/// </summary>
public sealed record AdminNavGroup(string LabelKey, IReadOnlyList<AdminNavItem> Items);

/// <summary>
/// A single sidebar entry. Exactly one of (Controller+Action) or RawHref is set.
/// Policy is preferred over RoleCheck — the latter exists for items that can't be
/// expressed as a single policy.
/// </summary>
public sealed record AdminNavItem(
    string LabelKey,
    string? Controller,
    string? Action,
    object? RouteValues,
    string? RawHref,
    string IconCssClass,
    string? Policy,
    Func<ClaimsPrincipal, bool>? RoleCheck = null,
    Func<IServiceProvider, ValueTask<int?>>? PillCount = null,
    Func<IWebHostEnvironment, bool>? EnvironmentGate = null);
