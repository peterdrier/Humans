using Microsoft.AspNetCore.Mvc;
using Humans.Users.Contracts;

namespace Humans.GoogleIntegration.ViewComponents;

/// <summary>
/// Modes: "badge" (icon), "status" (warning when not primary), "email" (the address), "detail" (address and badge,
/// or the provisioning form). Public for Razor's tag-helper discovery (HUM0034 exempts view components).
/// </summary>
public sealed class NobodiesEmailBadgeViewComponent(IUserService userService) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid userId, string mode = "badge")
    {
        var info = await userService.GetUserInfoAsync(userId, HttpContext.RequestAborted);
        var nobodies = info?.UserEmails.FirstOrDefault(e => e.IsVerified
            && e.Email.EndsWith("@nobodies.team", StringComparison.OrdinalIgnoreCase));

        ViewBag.UserId = info?.Id ?? userId;
        ViewBag.HasEmail = nobodies is not null;
        ViewBag.Email = nobodies?.Email;
        ViewBag.IsPrimary = nobodies?.IsPrimary == true;
        ViewBag.Mode = mode;

        return View();
    }
}
