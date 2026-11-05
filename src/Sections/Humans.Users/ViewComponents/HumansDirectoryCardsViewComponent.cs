using Microsoft.AspNetCore.Mvc;

namespace Humans.Users.ViewComponents;

/// <summary>Public because MVC only discovers public view components.</summary>
public sealed class HumansDirectoryCardsViewComponent : ViewComponent
{
    public IViewComponentResult Invoke() => View();
}
