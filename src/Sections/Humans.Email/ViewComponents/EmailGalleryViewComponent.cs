using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Email.ViewComponents;

/// <summary>A chrome-slot contribution takes no arguments, so it reads the signed-in user from claims.</summary>
internal sealed class EmailGalleryViewComponent : ViewComponent
{
    public IViewComponentResult Invoke() =>
        Guid.TryParse(UserClaimsPrincipal.FindFirstValue(ClaimTypes.NameIdentifier), out var currentUserId)
            ? View(currentUserId)
            : Content(string.Empty);
}
