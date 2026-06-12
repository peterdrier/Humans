using System.Security.Claims;
using Humans.Application.Interfaces;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Extensions;

namespace Humans.Web.Middleware;

/// <summary>
/// Tallies one page view per HTML response in <see cref="IClientStatsTracker"/>,
/// classifying the client by its User-Agent. Only <c>text/html</c> responses
/// count, so static assets, JSON APIs, health/metrics probes and the beacon
/// endpoint are naturally excluded. Feeds the <c>/Admin/ClientStats</c> screen.
/// Also records every error response (status &gt; 399, plus aborted requests as
/// 499) into the tracker's rolling buffer for <c>/Debug/HttpErrors</c>.
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

        Guid? userId = null;
        if (context.User.FindFirst(ClaimTypes.NameIdentifier) is { } claim
            && Guid.TryParse(claim.Value, out var id))
        {
            userId = id;
        }

        tracker.RecordError(new ClientErrorEntry(
            Timestamp: DateTimeOffset.UtcNow,
            StatusCode: aborted ? ClientClosedRequest : context.Response.StatusCode,
            Method: context.Request.Method,
            Url: context.Request.GetEncodedPathAndQuery(),
            IpAddress: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            UserId: userId,
            UserAgent: context.Request.Headers.UserAgent.ToString()));
    }
}
