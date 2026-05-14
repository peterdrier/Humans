using Humans.Domain.Entities;
using Microsoft.AspNetCore.Identity;
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
/// Don't write new direct <c>_userManager.GetUserAsync(User)</c> calls in
/// either kind of controller — use the helpers below (or the MVC
/// equivalents) so the user-resolution behavior stays uniform.
/// </para>
/// </summary>
public abstract class ApiControllerBase : ControllerBase
{
    private readonly UserManager<User> _userManager;

    protected UserManager<User> UserManager => _userManager;

    protected ApiControllerBase(UserManager<User> userManager)
    {
        _userManager = userManager;
    }

    protected Task<User?> GetCurrentUserAsync() => _userManager.GetUserAsync(User);

    protected Task<User?> FindUserByIdAsync(Guid userId) =>
        _userManager.FindByIdAsync(userId.ToString());

    /// <summary>
    /// Resolves the current user or returns 401 Unauthorized. Use this on
    /// authenticated API actions so the "auth cookie still valid but the
    /// user row is gone" race produces 401 rather than soft-failing into
    /// empty data. <see cref="Microsoft.AspNetCore.Authorization.AuthorizeAttribute"/>
    /// handles the no-cookie case at the framework layer; this helper
    /// covers the deleted-while-session-valid case at the action layer.
    /// </summary>
    protected async Task<(IActionResult? ErrorResult, User User)> ResolveCurrentUserOrUnauthorizedAsync()
    {
        var user = await GetCurrentUserAsync();
        return user is null ? (Unauthorized(), null!) : (null, user);
    }
}
