using System.Security.Claims;
using Humans.Web.Authorization;

namespace Humans.Web.ViewComponents;

public sealed record AdminNavGroup(string Label, IReadOnlyList<AdminNavItem> Items);

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
    Func<IWebHostEnvironment, bool>? EnvironmentGate = null);
