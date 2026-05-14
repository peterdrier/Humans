using Microsoft.AspNetCore.Diagnostics;

namespace Humans.Web.ExceptionHandlers;

/// <summary>
/// Swallows every <see cref="OperationCanceledException"/> that reaches the exception
/// pipeline. Inside a request scope an OCE is always non-actionable: either the client
/// aborted, the host is shutting down, or a linked/scope token tripped — in all cases
/// the response is going nowhere useful. Logs at Warning (without the exception object)
/// so the event is still visible in the prod log viewer per
/// <c>memory/code/always-log-problems.md</c>, and sets status 499 ("Client Closed
/// Request", nginx convention) when the response hasn't already started.
///
/// The earlier predicate required <see cref="HttpContext.RequestAborted"/> to be
/// flagged or the OCE's token to be reference-equal to it; EF/Npgsql often throw with
/// a linked token instead, so real aborts fell through to
/// <see cref="GlobalLoggingExceptionHandler"/> and surfaced as 500s. Registered BEFORE
/// <see cref="GlobalLoggingExceptionHandler"/> so cancellations never reach Error.
/// </summary>
public sealed class CancellationExceptionHandler : IExceptionHandler
{
    private const int ClientClosedRequest = 499;

    private readonly ILogger<CancellationExceptionHandler> _logger;

    public CancellationExceptionHandler(ILogger<CancellationExceptionHandler> logger)
        => _logger = logger;

    public ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not OperationCanceledException)
        {
            return ValueTask.FromResult(false);
        }

        _logger.LogWarning(
            "Request cancelled on {Method} {Path}",
            httpContext.Request.Method,
            httpContext.Request.Path);

        if (!httpContext.Response.HasStarted)
        {
            httpContext.Response.StatusCode = ClientClosedRequest;
        }

        return ValueTask.FromResult(true);
    }
}
