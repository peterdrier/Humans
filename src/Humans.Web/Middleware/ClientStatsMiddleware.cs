using Humans.Application.Interfaces;

namespace Humans.Web.Middleware;

/// <summary>
/// Tallies one page view per HTML response in <see cref="IClientStatsTracker"/>,
/// classifying the client by its User-Agent. Only <c>text/html</c> responses
/// count, so static assets, JSON APIs, health/metrics probes and the beacon
/// endpoint are naturally excluded. Feeds the <c>/Admin/ClientStats</c> screen.
/// </summary>
public sealed class ClientStatsMiddleware(RequestDelegate next, IClientStatsTracker tracker)
{
    public async Task InvokeAsync(HttpContext context)
    {
        await next(context);

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
    }
}
