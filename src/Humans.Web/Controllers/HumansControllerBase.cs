using System.Security.Claims;
using Humans.Application;
using Humans.Application.Interfaces.Users;
using Humans.Web.Constants;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

public abstract class HumansControllerBase : Controller
{
    private readonly IUserService _userService;
    protected IUserService UserService => _userService;

    protected HumansControllerBase(IUserService userService)
    {
        _userService = userService;
    }

    protected Guid? GetCurrentUserId()
    {
        var raw = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(raw, out var id) ? id : null;
    }

    protected async Task<UserInfo?> GetCurrentUserInfoAsync(CancellationToken ct = default)
    {
        var id = GetCurrentUserId();
        return id is null ? null : await _userService.GetUserInfoAsync(id.Value, ct);
    }

    protected async Task<UserInfo?> FindUserInfoByIdAsync(Guid userId, CancellationToken ct = default)
    {
        return await _userService.GetUserInfoAsync(userId, ct);
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
