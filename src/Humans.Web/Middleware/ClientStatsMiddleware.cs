using System.Security.Claims;
using Humans.Application.Interfaces;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Extensions;
using NodaTime;

namespace Humans.Web.Middleware;

/// <summary>
/// Tallies one page view per HTML response in <see cref="IClientStatsTracker"/>,
/// classifying the client by its User-Agent. Only <c>text/html</c> responses
/// count, so static assets, JSON APIs, health/metrics probes and the beacon
/// endpoint are naturally excluded. Feeds the <c>/Admin/ClientStats</c> screen.
/// Also records every error response (status &gt; 399, plus aborted requests as
/// 499) into the tracker's rolling buffer for <c>/Debug/HttpErrors</c>. 429s are
/// recorded by the rate limiter's OnRejected callback instead — its rejection
/// short-circuits before this middleware runs.
/// </summary>
public sealed class ClientStatsMiddleware(RequestDelegate next, IClientStatsTracker tracker)
{
    // Non-standard nginx code for "client closed request"; ASP.NET Core's request
    // metric reports aborted requests the same way, so the buffer stays comparable
    // to the ClientStats status-code tally.
    private const int ClientClosedRequest = 499;

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch when (context.RequestAborted.IsCancellationRequested)
        {
            // A client abort often surfaces downstream as a cancellation exception;
            // the request still ended, so record it before letting it propagate.
            MaybeRecordError(context);
            throw;
        }

        // Count only successful GET navigations that render HTML. This excludes
        // non-GET requests (e.g. a failed POST re-rendering a form) and re-executed
        // error pages — UseStatusCodePagesWithReExecute keeps the 4xx/5xx status on
        // the HTML error response — so the tally reflects real page views rather
        // than every HTML response.
        if (HttpMethods.IsGet(context.Request.Method)
            && context.Response.StatusCode is >= 200 and < 400
            && context.Response.ContentType is { } contentType
            && contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
        {
            tracker.RecordPageView(context.Request.Headers.UserAgent.ToString());
        }

        MaybeRecordError(context);
    }

    private void MaybeRecordError(HttpContext context)
    {
        var aborted = context.RequestAborted.IsCancellationRequested;
        if (context.Response.StatusCode <= 399 && !aborted)
            return;

        // UseStatusCodePagesWithReExecute re-runs the pipeline (and this middleware)
        // to render the error page; only the original pass records, or every 404
        // would appear twice — and with the error page's path instead of the real one.
        if (context.Features.Get<IStatusCodeReExecuteFeature>() is not null)
            return;

        tracker.RecordError(BuildEntry(
            context,
            aborted ? ClientClosedRequest : context.Response.StatusCode));
    }

    /// <summary>
    /// Builds a buffer entry from the request. Shared with the rate limiter's
    /// OnRejected callback in <c>Program.cs</c>, which records 429s this
    /// middleware never sees.
    /// </summary>
    internal static ClientErrorEntry BuildEntry(HttpContext context, int statusCode)
    {
        Guid? userId = null;
        if (context.User.FindFirst(ClaimTypes.NameIdentifier) is { } claim
            && Guid.TryParse(claim.Value, out var id))
        {
            userId = id;
        }

        // Unhandled exceptions are recorded during UseExceptionHandler's re-execute
        // at /Home/Error; the feature carries the failing request's real path.
        var url = context.Features.Get<IExceptionHandlerPathFeature>() is { } exceptionFeature
            ? exceptionFeature.Path
            : context.Request.GetEncodedPathAndQuery();

        return new ClientErrorEntry(
            Timestamp: SystemClock.Instance.GetCurrentInstant(),
            StatusCode: statusCode,
            Method: context.Request.Method,
            Url: url,
            IpAddress: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            UserId: userId,
            UserAgent: context.Request.Headers.UserAgent.ToString());
    }
}
