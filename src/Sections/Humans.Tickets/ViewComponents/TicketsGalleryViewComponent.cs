using Microsoft.AspNetCore.Mvc;

namespace Humans.Tickets.ViewComponents;

/// <summary>The widget gallery's Tickets samples: inline stub data plus the viewer-keyed cards.</summary>
internal sealed class TicketsGalleryViewComponent : ViewComponent
{
    public IViewComponentResult Invoke() => View();
}
