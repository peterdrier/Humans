using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.ViewComponents;

public class FeedbackWidgetViewComponent : ViewComponent
{
    public IViewComponentResult Invoke()
    {
        if (UserClaimsPrincipal?.Identity?.IsAuthenticated != true)
            return Content(string.Empty);

        return View();
    }
}
