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

        var contentType = context.Response.ContentType;
        if (contentType is not null
            && contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
        {
            tracker.RecordPageView(context.Request.Headers.UserAgent.ToString());
        }
    }
}
