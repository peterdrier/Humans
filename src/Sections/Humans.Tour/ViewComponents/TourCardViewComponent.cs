using Microsoft.AspNetCore.Mvc;

namespace Humans.Tour.ViewComponents;

/// <summary>Public because MVC only discovers public view components.</summary>
public sealed class TourCardViewComponent : ViewComponent
{
    public IViewComponentResult Invoke() => View();
}
