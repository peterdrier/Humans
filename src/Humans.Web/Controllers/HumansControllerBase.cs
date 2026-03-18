using Humans.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.Controllers;

public abstract class HumansControllerBase : Controller
{
    private readonly UserManager<User> _userManager;

    protected HumansControllerBase(UserManager<User> userManager)
    {
        _userManager = userManager;
    }

    protected Task<User?> GetCurrentUserAsync()
    {
        return _userManager.GetUserAsync(User);
    }

    protected async Task<(IActionResult? ErrorResult, User User)> ResolveCurrentUserAsync()
    {
        var user = await GetCurrentUserAsync();
        return user is null ? (NotFound(), null!) : (null, user);
    }

    protected async Task<(IActionResult? ErrorResult, User User)> RequireCurrentUserAsync()
    {
        var user = await GetCurrentUserAsync();
        return user == null ? (NotFound(), null!) : (null, user);
    }

    protected void SetSuccess(string message)
    {
        TempData["SuccessMessage"] = message;
    }

    protected void SetError(string message)
    {
        TempData["ErrorMessage"] = message;
    }

    protected void SetInfo(string message)
    {
        TempData["InfoMessage"] = message;
    }

    protected Task<IdentityResult> UpdateCurrentUserAsync(User user)
    {
        return _userManager.UpdateAsync(user);
    }
}
