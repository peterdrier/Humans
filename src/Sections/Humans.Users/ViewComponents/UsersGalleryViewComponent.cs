using System.Security.Claims;
using Humans.Users.Contracts;
using Humans.Users.Models;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Users.ViewComponents;

/// <summary>The widget gallery's Users-section samples, rendered against the signed-in admin.</summary>
/// <remarks>
/// A chrome-slot contribution takes no arguments, so this reads the current user from claims.
/// </remarks>
internal sealed class UsersGalleryViewComponent(IUserService userService) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        if (!Guid.TryParse(UserClaimsPrincipal.FindFirstValue(ClaimTypes.NameIdentifier), out var currentUserId))
            return Content(string.Empty);

        var info = await userService.GetUserInfoAsync(currentUserId, HttpContext.RequestAborted);
        var displayName = string.IsNullOrEmpty(info?.BurnerName) ? "Current user" : info.BurnerName;
        var sample = new ProfileSummaryViewModel
        {
            UserId = currentUserId,
            DisplayName = displayName,
            Email = info?.Email,
            MembershipStatus = "Active",
            MembershipTier = "Volunteer",
            IsSuspended = false,
            PreferredLanguage = info?.PreferredLanguage,
            Teams = ["Fire Conclave"],
        };
        return View(new UsersGalleryViewModel(currentUserId, sample));
    }
}

internal sealed record UsersGalleryViewModel(Guid CurrentUserId, ProfileSummaryViewModel Sample);
