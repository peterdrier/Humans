using Microsoft.AspNetCore.Diagnostics;

namespace Humans.Web.ExceptionHandlers;

/// <summary>
/// Catch-all logger for unhandled exceptions. Logs with method/path/controller/action
/// context, then returns <c>false</c> so the rest of the pipeline (in particular
/// <c>UseExceptionHandler("/Home/Error")</c>) still gets to render the error page.
///
/// Registered AFTER <see cref="CancellationExceptionHandler"/> so client-cancelled
/// requests are already handled and do not get logged at Error level.
/// </summary>
public sealed class GlobalLoggingExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GlobalLoggingExceptionHandler> _logger;

    public GlobalLoggingExceptionHandler(ILogger<GlobalLoggingExceptionHandler> logger)
        => _logger = logger;

    public ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var routeValues = httpContext.Request.RouteValues;
        var controller = routeValues.TryGetValue("controller", out var c) ? c : null;
        var action = routeValues.TryGetValue("action", out var a) ? a : null;

        _logger.LogError(
            exception,
            "Unhandled exception on {Method} {Path} (controller={Controller}, action={Action})",
            httpContext.Request.Method,
            httpContext.Request.Path,
            controller,
            action);

        // Return false so UseExceptionHandler("/Home/Error") still runs and renders
        // the error page to the user.
        return ValueTask.FromResult(false);
    }
}
