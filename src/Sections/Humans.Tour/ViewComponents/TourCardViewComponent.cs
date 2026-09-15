using Microsoft.AspNetCore.Mvc;

namespace Humans.Tour.ViewComponents;

/// <summary>
/// The dashboard card that opens /Tour. Static like the page itself — it injects nothing and
/// reads no member data. Public because MVC only discovers public view components (HUM0034's
/// framework exception).
/// </summary>
public sealed class TourCardViewComponent : ViewComponent
{
    public IViewComponentResult Invoke() => View();
}
