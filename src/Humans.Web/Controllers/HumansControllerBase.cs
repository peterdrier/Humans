using Humans.Domain.Entities;
using Humans.Web.Constants;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

public abstract class HumansControllerBase : Controller
{
    private readonly UserManager<User> _userManager;
    protected UserManager<User> UserManager => _userManager;

    protected HumansControllerBase(UserManager<User> userManager)
    {
        _userManager = userManager;
    }

    protected Task<User?> GetCurrentUserAsync()
    {
        return _userManager.GetUserAsync(User);
    }

    protected Task<User?> FindUserByIdAsync(Guid userId)
    {
        return _userManager.FindByIdAsync(userId.ToString());
    }

    protected async Task<(IActionResult? ErrorResult, User User)> RequireCurrentUserAsync()
    {
        return await ResolveCurrentUserAsync(() => NotFound());
    }

    protected async Task<(IActionResult? ErrorResult, User User)> ResolveCurrentUserOrChallengeAsync()
    {
        return await ResolveCurrentUserAsync(() => Challenge());
    }

    protected async Task<(IActionResult? ErrorResult, User User)> ResolveCurrentUserOrUnauthorizedAsync()
    {
        return await ResolveCurrentUserAsync(() => Unauthorized());
    }

    private async Task<(IActionResult? ErrorResult, User User)> ResolveCurrentUserAsync(Func<IActionResult> onMissing)
    {
        var user = await GetCurrentUserAsync();
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

    protected Task<IdentityResult> UpdateCurrentUserAsync(User user)
    {
        return _userManager.UpdateAsync(user);
    }
}
