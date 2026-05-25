using System.Security.Claims;
using Humans.Application;
using Humans.Application.Interfaces.Users;
using Humans.Web.Constants;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

public abstract class HumansControllerBase(IUserServiceRead userService) : Controller
{
    protected IUserServiceRead UserService => userService;

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
        var logger = HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger(GetType());
        logger.LogDebug("Error toast: {Message} (Action: {Action})", message, ControllerContext.ActionDescriptor.ActionName);
        TempData[TempDataKeys.ErrorMessage] = message;
    }

    protected void SetInfo(string message)
    {
        TempData[TempDataKeys.InfoMessage] = message;
    }
}
