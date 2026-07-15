using AwesomeAssertions;
using Humans.Infrastructure.Services;
using Humans.Web.Middleware;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Humans.Web.Tests.Services;

public class ClientStatsMiddlewareTests
{
    private const string WinChrome = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    private static async Task<ClientStatsTracker> RunAsync(
        string method,
        int status,
        string? contentType,
        Action<HttpContext>? configure = null)
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
        context.Request.Path = "/some/path";
        context.Request.Headers.UserAgent = WinChrome;
        configure?.Invoke(context);

        await middleware.InvokeAsync(context);

        return tracker;
    }

    private static async Task<long> PageViewsAsync(string method, int status, string? contentType)
        => (await RunAsync(method, status, contentType)).GetSnapshot().TotalPageViews;

    [HumansFact]
    public async Task SuccessfulGetHtml_IsCounted()
        => (await PageViewsAsync(HttpMethods.Get, 200, "text/html; charset=utf-8")).Should().Be(1);

    [HumansFact]
    public async Task PostHtml_IsNotCounted()
        => (await PageViewsAsync(HttpMethods.Post, 200, "text/html; charset=utf-8")).Should().Be(0);

    [HumansFact]
    public async Task GetErrorPage_IsNotCounted()
        => (await PageViewsAsync(HttpMethods.Get, 404, "text/html; charset=utf-8")).Should().Be(0);

    [HumansFact]
    public async Task GetJson_IsNotCounted()
        => (await PageViewsAsync(HttpMethods.Get, 200, "application/json")).Should().Be(0);

    [HumansFact]
    public async Task ErrorResponse_IsRecordedWithRequestDetails()
    {
        var tracker = await RunAsync(HttpMethods.Post, 404, null,
            ctx => ctx.Request.QueryString = new QueryString("?q=1"));

        var snap = tracker.GetErrorsSnapshot(10);
        var entry = snap.Recent.Should().ContainSingle().Subject;
        entry.StatusCode.Should().Be(404);
        entry.Method.Should().Be(HttpMethods.Post);
        entry.Url.Should().Be("/some/path?q=1");
        entry.UserAgent.Should().Be(WinChrome);
        snap.LifetimeCounts[404].Should().Be(1);
    }

    [HumansFact]
    public async Task ServerError_IsRecorded()
    {
        var tracker = await RunAsync(HttpMethods.Get, 500, null);

        tracker.GetErrorsSnapshot(10).Recent.Should().ContainSingle()
            .Which.StatusCode.Should().Be(500);
    }

    [HumansFact]
    public async Task SuccessResponse_IsNotRecorded()
    {
        var tracker = await RunAsync(HttpMethods.Get, 200, "text/html");

        tracker.GetErrorsSnapshot(10).Recent.Should().BeEmpty();
    }

    [HumansFact]
    public async Task AbortedRequest_IsRecordedAs499()
    {
        var tracker = await RunAsync(HttpMethods.Get, 200, "text/html",
            ctx => ctx.RequestAborted = new CancellationToken(canceled: true));

        tracker.GetErrorsSnapshot(10).Recent.Should().ContainSingle()
            .Which.StatusCode.Should().Be(499);
    }

    [HumansFact]
    public async Task AbortedProfilePicture_AuthenticatedUser_IsNotRecorded()
    {
        var tracker = await RunAsync(HttpMethods.Get, 200, "image/webp", ctx =>
        {
            ctx.Request.Path = "/Profile/Picture";
            ctx.Request.QueryString = new QueryString("?id=abc&v=1");
            ctx.RequestAborted = new CancellationToken(canceled: true);
            ctx.User = AuthenticatedUser();
        });

        tracker.GetErrorsSnapshot(10).Recent.Should().BeEmpty();
    }

    [HumansFact]
    public async Task AbortedProfilePicture_AnonymousUser_IsRecordedAs499()
    {
        var tracker = await RunAsync(HttpMethods.Get, 200, "image/webp", ctx =>
        {
            ctx.Request.Path = "/Profile/Picture";
            ctx.RequestAborted = new CancellationToken(canceled: true);
        });

        tracker.GetErrorsSnapshot(10).Recent.Should().ContainSingle()
            .Which.StatusCode.Should().Be(499);
    }

    [HumansFact]
    public async Task AbortedNonPicturePath_AuthenticatedUser_IsStillRecorded()
    {
        var tracker = await RunAsync(HttpMethods.Get, 200, "text/html", ctx =>
        {
            ctx.RequestAborted = new CancellationToken(canceled: true);
            ctx.User = AuthenticatedUser();
        });

        tracker.GetErrorsSnapshot(10).Recent.Should().ContainSingle()
            .Which.StatusCode.Should().Be(499);
    }

    private static System.Security.Claims.ClaimsPrincipal AuthenticatedUser()
        => new(new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim(
                System.Security.Claims.ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())],
            authenticationType: "Test"));

    [HumansFact]
    public async Task ExceptionHandlerReExecute_RecordsOriginalPath()
    {
        var tracker = await RunAsync(HttpMethods.Get, 500, "text/html",
            ctx => ctx.Features.Set<IExceptionHandlerPathFeature>(
                new ExceptionHandlerFeature { Path = "/teams/broken", Error = new InvalidOperationException() }));

        tracker.GetErrorsSnapshot(10).Recent.Should().ContainSingle()
            .Which.Url.Should().Be("/teams/broken");
    }

    [HumansFact]
    public async Task StatusCodeReExecutePass_IsNotRecorded()
    {
        var tracker = await RunAsync(HttpMethods.Get, 404, "text/html",
            ctx => ctx.Features.Set<IStatusCodeReExecuteFeature>(
                new StatusCodeReExecuteFeature { OriginalPath = "/missing" }));

        tracker.GetErrorsSnapshot(10).Recent.Should().BeEmpty();
    }

    [HumansFact]
    public async Task AbortedRequest_ThrowingCancellation_IsRecordedAndRethrown()
    {
        var tracker = new ClientStatsTracker();
        var middleware = new ClientStatsMiddleware(
            _ => throw new OperationCanceledException(), tracker);

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/slow";
        context.RequestAborted = new CancellationToken(canceled: true);

        await Assert.ThrowsAsync<OperationCanceledException>(() => middleware.InvokeAsync(context));

        tracker.GetErrorsSnapshot(10).Recent.Should().ContainSingle()
            .Which.StatusCode.Should().Be(499);
    }
}
