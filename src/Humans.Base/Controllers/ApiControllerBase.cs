using System.Security.Claims;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Base.Controllers;

/// <summary>
/// Base class for JSON API controllers; MVC controllers that render views or use TempData take
/// <see cref="HumansControllerBase"/> (<c>memory/code/controller-base-conventions.md</c>). Use the cache-resident
/// helpers below, not new <c>UserManager.GetUserAsync(User)</c> calls; an action that needs a tracked <c>User</c>
/// entity injects <c>UserManager</c> itself.
/// Authenticated by default; a public action or controller says so with <c>[AllowAnonymous]</c>.
/// </summary>
[Authorize]
public abstract class ApiControllerBase(IUserService userService) : ControllerBase
{
    protected IUserService UserService => userService;

    protected Guid? GetCurrentUserId()
    {
        var raw = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(raw, out var id) ? id : null;
    }

    protected async Task<UserInfo?> GetCurrentUserInfoAsync(CancellationToken ct = default)
    {
        var id = GetCurrentUserId();
        return id is null ? null : await userService.GetUserInfoAsync(id.Value, ct);
    }

    protected async Task<UserInfo?> FindUserInfoByIdAsync(Guid userId, CancellationToken ct = default)
    {
        return await userService.GetUserInfoAsync(userId, ct);
    }

    /// <summary>
    /// Resolves the current user or returns 401 Unauthorized. Use this on
    /// authenticated API actions so the "auth cookie still valid but the
    /// user row is gone" race produces 401 rather than soft-failing into
    /// empty data. <see cref="Microsoft.AspNetCore.Authorization.AuthorizeAttribute"/>
    /// handles the no-cookie case at the framework layer; this helper
    /// covers the deleted-while-session-valid case at the action layer.
    /// </summary>
    protected async Task<(IActionResult? ErrorResult, UserInfo User)> ResolveCurrentUserOrUnauthorizedAsync(CancellationToken ct = default)
    {
        var user = await GetCurrentUserInfoAsync(ct);
        return user is null ? (Unauthorized(), null!) : (null, user);
    }
}
