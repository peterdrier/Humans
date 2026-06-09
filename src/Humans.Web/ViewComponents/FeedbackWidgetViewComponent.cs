using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.ViewComponents;

public class FeedbackWidgetViewComponent : ViewComponent
{
    public IViewComponentResult Invoke()
        => Content(string.Empty);
}
