using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Humans.Web.Filters;

/// <summary>
/// Action filter that returns 404 when the Event Guide feature is disabled
/// via the <c>Features:Events</c> configuration flag.
/// </summary>
public class EventsFeatureFilter(IConfiguration configuration) : IActionFilter
{
    private readonly bool _enabled = configuration.GetValue<bool>("Features:Events");

    public void OnActionExecuting(ActionExecutingContext context)
    {
        if (!_enabled)
            context.Result = new NotFoundResult();
    }

    public void OnActionExecuted(ActionExecutedContext context) { }
}
