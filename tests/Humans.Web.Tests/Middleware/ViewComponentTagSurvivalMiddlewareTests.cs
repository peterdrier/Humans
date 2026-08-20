using System.Text;
using AwesomeAssertions;
using Humans.Web.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Humans.Web.Tests.Middleware;

/// <summary>
/// Proves the Dev/QA response scanner (nobodies-collective/Humans#1055) actually fires on the
/// exact failure shape it exists for — an unresolved <c>&lt;vc:...&gt;</c> element in the
/// rendered HTML — and stays silent on a clean response, so it is neither a no-op nor a
/// false-positive machine.
/// </summary>
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
    public async Task NonHtmlNavigationRequest_NeverBuffers_EvenWithASurvivingElement()
    {
        // Neither a browser page navigation (no GET, or no text/html Accept) — e.g. a JSON
        // API call or a file download — must skip the buffer/scan entirely.
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Headers.Accept = "application/json";
        context.Response.Body = new MemoryStream();

        var logger = new CapturingLogger<ViewComponentTagSurvivalMiddleware>();
        var bodyPassedThrough = false;
        var middleware = new ViewComponentTagSurvivalMiddleware(
            ctx =>
            {
                // If the middleware buffered, Response.Body would be a MemoryStream it owns,
                // not the one this test assigned above.
                bodyPassedThrough = ReferenceEquals(ctx.Response.Body, context.Response.Body);
                return Task.CompletedTask;
            },
            logger);

        await middleware.InvokeAsync(context);

        bodyPassedThrough.Should().BeTrue("a non-navigation request must reach `next` with the original body untouched");
        logger.Entries.Should().BeEmpty();
    }
}
