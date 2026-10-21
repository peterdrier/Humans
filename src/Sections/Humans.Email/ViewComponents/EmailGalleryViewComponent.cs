using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Email.ViewComponents;

/// <summary>
/// The widget gallery's Email sample, contributed to
/// <see cref="Humans.Base.Interfaces.ChromeSlots.WidgetGallery"/> via <see cref="SectionChrome"/>:
/// &lt;vc:communication-preferences-panel&gt; rendered against the signed-in admin. A chrome-slot
/// contribution takes no arguments, so it reads the current user from claims.
/// </summary>
internal sealed class EmailGalleryViewComponent : ViewComponent
{
    public IViewComponentResult Invoke() =>
        Guid.TryParse(UserClaimsPrincipal.FindFirstValue(ClaimTypes.NameIdentifier), out var currentUserId)
            ? View(currentUserId)
            : Content(string.Empty);
}
