using System.Security.Claims;
using Humans.Application;
using Humans.Application.Interfaces.Users;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

/// <summary>
/// Base class for JSON API controllers. Inherit from this instead of
/// <see cref="ControllerBase"/> so authenticated-user resolution stays
/// consistent across the API surface without dragging in the
/// view-rendering / TempData machinery that <see cref="HumansControllerBase"/>
/// provides for server-rendered MVC controllers.
///
/// <para>
/// Convention (see <c>memory/code/controller-base-conventions.md</c>):
/// <list type="bullet">
///   <item>MVC controllers returning views or using TempData → <see cref="HumansControllerBase"/>.</item>
///   <item>API controllers returning JSON via <c>IActionResult</c> / <c>ActionResult&lt;T&gt;</c> → this class.</item>
/// </list>
/// Don't write new direct <c>UserManager.GetUserAsync(User)</c> calls in
/// either kind of controller — use the cache-resident helpers below.
/// Actions that genuinely need a tracked <c>User</c> entity (Identity
/// mutations, sign-in flows) should self-inject <c>UserManager</c> in
/// the derived controller.
/// </para>
/// </summary>
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
