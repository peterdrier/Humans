using Microsoft.AspNetCore.Diagnostics;

namespace Humans.Web.ExceptionHandlers;

/// <summary>
/// Logs unhandled exceptions with request context, then returns <c>false</c> so
/// <c>UseExceptionHandler("/Home/Error")</c> still renders the error page.
/// </summary>
public sealed class GlobalLoggingExceptionHandler(ILogger<GlobalLoggingExceptionHandler> logger) : IExceptionHandler
{
    public ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var routeValues = httpContext.Request.RouteValues;
        var controller = routeValues.TryGetValue("controller", out var c) ? c : null;
        var action = routeValues.TryGetValue("action", out var a) ? a : null;

        logger.LogError(
            exception,
            "Unhandled exception on {Method} {Path} (controller={Controller}, action={Action})",
            httpContext.Request.Method,
            httpContext.Request.Path,
            controller,
            action);

        return ValueTask.FromResult(false);
    }
}
