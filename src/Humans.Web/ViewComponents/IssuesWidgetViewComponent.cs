using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.ViewComponents;

/// <summary>
/// Floating "file an issue" widget rendered on every page of the member shell
/// (and the admin shell). Replaces the old &lt;vc:feedback-widget /&gt; cut-over.
/// Posts AJAX multipart/form-data to <c>/Issues</c> (the IssuesController.Submit
/// action returns JSON when X-Requested-With: XMLHttpRequest is present).
/// </summary>
public class IssuesWidgetViewComponent : ViewComponent
{
    public IViewComponentResult Invoke()
    {
        if (UserClaimsPrincipal?.Identity?.IsAuthenticated != true)
            return Content(string.Empty);

        // Pre-fill the section dropdown from the page the widget was opened on
        // (controller still re-infers via IssueSectionInference if Section is null).
        var pagePath = Request?.Path.Value ?? string.Empty;
        return View(model: pagePath);
    }
}
