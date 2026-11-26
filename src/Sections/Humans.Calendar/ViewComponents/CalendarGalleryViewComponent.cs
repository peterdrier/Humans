using Humans.Base.Extensions;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Calendar.ViewComponents;

/// <summary>The widget gallery's Calendar sample. A chrome-slot contribution takes no arguments, so it reads the current user from claims.</summary>
internal sealed class CalendarGalleryViewComponent : ViewComponent
{
    public IViewComponentResult Invoke() =>
        UserClaimsPrincipal.GetUserId() is { } currentUserId
            ? View(currentUserId)
            : Content(string.Empty);
}
