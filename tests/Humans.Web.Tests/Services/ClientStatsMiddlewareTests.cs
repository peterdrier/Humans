using AwesomeAssertions;
using Humans.Infrastructure.Services;
using Humans.Testing;
using Humans.Web.Middleware;
using Microsoft.AspNetCore.Http;

namespace Humans.Web.Tests.Services;

public class ClientStatsMiddlewareTests
{
    private const string WinChrome = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    private static async Task<long> RunAsync(string method, int status, string? contentType)
    {
        var tracker = new ClientStatsTracker();
        var middleware = new ClientStatsMiddleware(ctx =>
        {
            ctx.Response.StatusCode = status;
            if (contentType is not null) ctx.Response.ContentType = contentType;
            return Task.CompletedTask;
        }, tracker);

        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Headers.UserAgent = WinChrome;

        await middleware.InvokeAsync(context);

        return tracker.GetSnapshot().TotalPageViews;
    }

    [HumansFact]
    public async Task SuccessfulGetHtml_IsCounted()
        => (await RunAsync(HttpMethods.Get, 200, "text/html; charset=utf-8")).Should().Be(1);

    [HumansFact]
    public async Task PostHtml_IsNotCounted()
        => (await RunAsync(HttpMethods.Post, 200, "text/html; charset=utf-8")).Should().Be(0);

    [HumansFact]
    public async Task GetErrorPage_IsNotCounted()
        => (await RunAsync(HttpMethods.Get, 404, "text/html; charset=utf-8")).Should().Be(0);

    [HumansFact]
    public async Task GetJson_IsNotCounted()
        => (await RunAsync(HttpMethods.Get, 200, "application/json")).Should().Be(0);
}
