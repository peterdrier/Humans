using Microsoft.AspNetCore.Mvc;

namespace Humans.Consent.ViewComponents;

/// <summary>The widget gallery's Consent cards.</summary>
internal sealed class ConsentGalleryViewComponent : ViewComponent
{
    public IViewComponentResult Invoke() => View();
}
