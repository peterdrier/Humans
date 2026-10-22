using Microsoft.AspNetCore.Diagnostics;

namespace Humans.Web.ExceptionHandlers;

/// <summary>
/// Swallows every <see cref="OperationCanceledException"/> reaching the pipeline: inside a request it is never
/// actionable. Sets 499 if the response hasn't started, and logs nothing; code that adds a real server-side timeout
/// must log it before letting the exception escape. Registered before <see cref="GlobalLoggingExceptionHandler"/>.
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
