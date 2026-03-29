using Microsoft.AspNetCore.Mvc.Filters;

namespace Humans.Web.Filters;

/// <summary>
/// Global MVC exception filter — logs unhandled exceptions at the controller/view level
/// before they bubble up to middleware. Safety net for exceptions that might otherwise
/// be swallowed during view rendering.
/// </summary>
public class GlobalExceptionFilter : IExceptionFilter
{
    private readonly ILogger<GlobalExceptionFilter> _logger;

    public GlobalExceptionFilter(ILogger<GlobalExceptionFilter> logger)
        => _logger = logger;

    public void OnException(ExceptionContext context)
    {
        _logger.LogError(context.Exception,
            "Unhandled exception in {Controller}.{Action} on {Path}",
            context.RouteData.Values["controller"],
            context.RouteData.Values["action"],
            context.HttpContext.Request.Path);
    }
}
