using Microsoft.AspNetCore.Diagnostics;

namespace Humans.Web.ExceptionHandlers;

/// <summary>
/// Swallows <see cref="OperationCanceledException"/> when the cancellation originated
/// from the client aborting the request (<see cref="HttpContext.RequestAborted"/>).
/// Logs at Information since this is expected behaviour, sets status 499
/// ("Client Closed Request", an nginx convention) when the response hasn't
/// already started, and returns <c>true</c> to short-circuit further handlers.
///
/// Registered BEFORE <see cref="GlobalLoggingExceptionHandler"/> so cancellations
/// never get logged as errors.
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
        if (exception is not OperationCanceledException oce)
        {
            return ValueTask.FromResult(false);
        }

        // Treat as a client abort when either:
        //  - the request has been aborted (RequestAborted flagged), or
        //  - the OCE carries the request's cancellation token (Npgsql and other
        //    DB-level cancellations propagate this even when RequestAborted
        //    hasn't been observed yet on this thread).
        // This second branch covers `NpgsqlOperationCanceledException` from
        // mid-query aborts where the request token tripped Npgsql before the
        // pipeline had a chance to flag RequestAborted.
        var isClientAbort =
            httpContext.RequestAborted.IsCancellationRequested
            || oce.CancellationToken == httpContext.RequestAborted;

        if (!isClientAbort)
        {
            // Server-initiated cancellation — let other handlers log/handle.
            return ValueTask.FromResult(false);
        }

        _logger.LogInformation(
            "Request cancelled by client on {Method} {Path}",
            httpContext.Request.Method,
            httpContext.Request.Path);

        if (!httpContext.Response.HasStarted)
        {
            httpContext.Response.StatusCode = ClientClosedRequest;
        }

        return ValueTask.FromResult(true);
    }
}
