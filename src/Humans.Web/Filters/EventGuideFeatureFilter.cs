using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Humans.Web.Filters;

/// <summary>
/// Action filter that returns 404 when the Event Guide feature is disabled
/// via the <c>Features:EventGuide</c> configuration flag.
/// </summary>
public class EventGuideFeatureFilter : IActionFilter
{
    private readonly bool _enabled;

    public EventGuideFeatureFilter(IConfiguration configuration)
        => _enabled = configuration.GetValue<bool>("Features:EventGuide");

    public void OnActionExecuting(ActionExecutingContext context)
    {
        if (!_enabled)
            context.Result = new NotFoundResult();
    }

    public void OnActionExecuted(ActionExecutedContext context) { }
}
