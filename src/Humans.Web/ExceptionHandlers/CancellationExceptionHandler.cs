using Microsoft.AspNetCore.Diagnostics;

namespace Humans.Web.ExceptionHandlers;

/// <summary>
/// Swallows every <see cref="OperationCanceledException"/> that reaches the exception
/// pipeline. Inside a request scope an OCE is always non-actionable: either the client
/// aborted, the host is shutting down, or a linked/scope token tripped — in all cases
/// the response is going nowhere useful. Sets status 499 ("Client Closed Request",
/// nginx convention) when the response hasn't already started and emits no log: the
/// signal value of "yet another tab closed" is zero, and was producing 50+ Error/Warning
/// events per triage window. If a real server-side timeout ever needs Error-level
/// attention, the code introducing the timeout should log it directly before letting
/// the OCE escape.
///
/// Registered BEFORE <see cref="GlobalLoggingExceptionHandler"/> so cancellations
/// never reach Error.
/// </summary>
public sealed class CancellationExceptionHandler : IExceptionHandler
{
    private const int ClientClosedRequest = 499;

    public ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not OperationCanceledException)
        {
            return ValueTask.FromResult(false);
        }

        if (!httpContext.Response.HasStarted)
        {
            httpContext.Response.StatusCode = ClientClosedRequest;
        }

        return ValueTask.FromResult(true);
    }
}
