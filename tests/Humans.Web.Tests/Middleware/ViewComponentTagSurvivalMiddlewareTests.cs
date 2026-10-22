using System.Text;
using AwesomeAssertions;
using Humans.Web.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Humans.Web.Tests.Middleware;

public class ViewComponentTagSurvivalMiddlewareTests
{
    private static async Task<CapturingLogger<ViewComponentTagSurvivalMiddleware>> RunAsync(
        HttpContext context,
        string responseBody,
        string contentType = "text/html; charset=utf-8")
    {
        var logger = new CapturingLogger<ViewComponentTagSurvivalMiddleware>();
        var middleware = new ViewComponentTagSurvivalMiddleware(
            async ctx =>
            {
                ctx.Response.ContentType = contentType;
                var bytes = Encoding.UTF8.GetBytes(responseBody);
                await ctx.Response.Body.WriteAsync(bytes);
            },
            logger);

        await middleware.InvokeAsync(context);
        return logger;
    }

    private static DefaultHttpContext HtmlNavigationRequest()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Headers.Accept = "text/html,application/xhtml+xml";
        context.Request.Path = "/Profile/00000000-0000-0000-0000-000000000001";
        context.Response.Body = new MemoryStream();
        return context;
    }

    [HumansFact]
    public async Task SurvivingVcElement_LogsAWarningNamingPathAndElement()
    {
        var context = HtmlNavigationRequest();

        var logger = await RunAsync(
            context,
            "<html><body><h1>Profile</h1><vc:profile-card></body></html>");

        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Warning);
        entry.Message.Should().Contain("/Profile/00000000-0000-0000-0000-000000000001");
        entry.Message.Should().Contain("<vc:profile-card>");
    }

    [HumansFact]
    public async Task CleanHtmlWithNoVcElement_LogsNothing()
    {
        var context = HtmlNavigationRequest();

        var logger = await RunAsync(
            context,
            "<html><body><h1>Profile</h1><p>Nothing to see here.</p></body></html>");

        logger.Entries.Should().BeEmpty(
            "a detector that fires on every response proves nothing — it must stay quiet on clean output");
    }

    [HumansFact]
    public async Task ResponseBody_IsAlwaysForwardedToTheClientUnchanged()
    {
        var context = HtmlNavigationRequest();
        const string body = "<html><body><vc:profile-card></body></html>";

        await RunAsync(context, body);

        context.Response.Body.Position = 0;
        var written = await new StreamReader(context.Response.Body).ReadToEndAsync();
        written.Should().Be(body, "scanning must never alter what the browser actually receives");
    }

    [HumansFact]
    public async Task Download_StreamsStraightThrough_EvenThoughTheBrowserAsksForItWithANavigationAcceptHeader()
    {
        // A download link sends the same Accept: text/html as a navigation; only the response Content-Type tells them
        // apart, so a large export must never be buffered here.
        var context = HtmlNavigationRequest();
        var client = (MemoryStream)context.Response.Body;
        const string csv = "id,answer\n1,yes\n";

        var logger = new CapturingLogger<ViewComponentTagSurvivalMiddleware>();
        var reachedClientBeforeNextReturned = false;
        var middleware = new ViewComponentTagSurvivalMiddleware(
            async ctx =>
            {
                ctx.Response.ContentType = "text/csv";
                await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(csv));
                reachedClientBeforeNextReturned = client.Length == csv.Length;
            },
            logger);

        await middleware.InvokeAsync(context);

        reachedClientBeforeNextReturned.Should().BeTrue(
            "a non-HTML response must stream to the client as it is written, not be held until the pipeline unwinds");
        Encoding.UTF8.GetString(client.ToArray()).Should().Be(csv);
        logger.Entries.Should().BeEmpty();
    }

    [HumansFact]
    public async Task NonGetRequest_ReachesNextWithTheResponseBodyUntouched()
    {
        // No page renders on a POST, so the response body is never wrapped at all.
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Headers.Accept = "application/json";
        context.Response.Body = new MemoryStream();

        var logger = new CapturingLogger<ViewComponentTagSurvivalMiddleware>();
        var bodyPassedThrough = false;
        var middleware = new ViewComponentTagSurvivalMiddleware(
            ctx =>
            {
                // If the middleware wrapped it, Response.Body would be the middleware's own
                // stream, not the one this test assigned above.
                bodyPassedThrough = ReferenceEquals(ctx.Response.Body, context.Response.Body);
                return Task.CompletedTask;
            },
            logger);

        await middleware.InvokeAsync(context);

        bodyPassedThrough.Should().BeTrue("a non-GET request must reach `next` with the original body untouched");
        logger.Entries.Should().BeEmpty();
    }
}
