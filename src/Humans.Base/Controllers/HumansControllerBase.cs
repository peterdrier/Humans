using System.Security.Claims;
using Humans.Base.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Humans.Users.Contracts;

namespace Humans.Base.Controllers;

/// <summary>Authenticated by default; a public action or controller says so with <c>[AllowAnonymous]</c>.</summary>
[Authorize]
public abstract class HumansControllerBase(IUserService userService) : Controller
{
    protected IUserService UserService => userService;

    protected Guid? GetCurrentUserId()
    {
        var raw = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(raw, out var id) ? id : null;
    }

    protected bool IsAuthenticated()
    {
        return User.Identity?.IsAuthenticated == true;
    }

    protected async ValueTask<UserInfo?> GetCurrentUserInfoAsync(CancellationToken ct = default)
    {
        var id = GetCurrentUserId();
        return id is null ? null : await userService.GetUserInfoAsync(id.Value, ct);
    }

    protected async ValueTask<UserInfo?> FindUserInfoByIdAsync(Guid userId, CancellationToken ct = default)
    {
        return await userService.GetUserInfoAsync(userId, ct);
    }

    protected async Task<(IActionResult? ErrorResult, UserInfo User)> RequireCurrentUserAsync(CancellationToken ct = default)
    {
        return await ResolveCurrentUserAsync(NotFound, ct);
    }

    protected async Task<(IActionResult? ErrorResult, UserInfo User)> ResolveCurrentUserOrChallengeAsync(CancellationToken ct = default)
    {
        return await ResolveCurrentUserAsync(Challenge, ct);
    }

    protected async Task<(IActionResult? ErrorResult, UserInfo User)> ResolveCurrentUserOrUnauthorizedAsync(CancellationToken ct = default)
    {
        return await ResolveCurrentUserAsync(Unauthorized, ct);
    }

    private async Task<(IActionResult? ErrorResult, UserInfo User)> ResolveCurrentUserAsync(Func<IActionResult> onMissing, CancellationToken ct)
    {
        var user = await GetCurrentUserInfoAsync(ct);
        return user is null ? (onMissing(), null!) : (null, user);
    }

    protected void SetSuccess(string message)
    {
        TempData[TempDataKeys.SuccessMessage] = message;
    }

    protected void SetError(string message)
    {
        TempData[TempDataKeys.ErrorMessage] = message;
    }

    protected void SetInfo(string message)
    {
        TempData[TempDataKeys.InfoMessage] = message;
    }
}
