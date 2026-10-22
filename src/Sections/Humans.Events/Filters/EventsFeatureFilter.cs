using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Configuration;

namespace Humans.Events.Filters;

/// <summary>404 while the <c>Features:Events</c> flag is off.</summary>
internal sealed class EventsFeatureFilter(IConfiguration configuration) : IActionFilter
{
    private readonly bool _enabled = configuration.GetValue<bool>("Features:Events");

    public void OnActionExecuting(ActionExecutingContext context)
    {
        if (!_enabled)
            context.Result = new NotFoundResult();
    }

    public void OnActionExecuted(ActionExecutedContext context) { }
}
