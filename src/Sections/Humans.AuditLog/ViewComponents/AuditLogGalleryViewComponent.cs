using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;

namespace Humans.AuditLog.ViewComponents;

/// <summary>The widget gallery's AuditLog sample. A chrome-slot contribution takes no arguments, so it reads the current user from claims.</summary>
internal sealed class AuditLogGalleryViewComponent : ViewComponent
{
    public IViewComponentResult Invoke() =>
        Guid.TryParse(UserClaimsPrincipal.FindFirstValue(ClaimTypes.NameIdentifier), out var currentUserId)
            ? View(currentUserId)
            : Content(string.Empty);
}
