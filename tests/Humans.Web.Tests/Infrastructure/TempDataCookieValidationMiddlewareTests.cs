using AwesomeAssertions;
using Humans.Web.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Xunit;

namespace Humans.Web.Tests.Infrastructure;

public class TempDataCookieValidationMiddlewareTests
{
    private const string CookieName = ".AspNetCore.Mvc.CookieTempDataProvider";

    private static async Task<(bool NextCalled, CapturingLogger<TempDataCookieValidationMiddleware> Logger)> RunAsync(
        HttpContext context,
        IOptions<CookieTempDataProviderOptions>? options = null)
    {
        var logger = new CapturingLogger<TempDataCookieValidationMiddleware>();
        var nextCalled = false;
        var middleware = new TempDataCookieValidationMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            options ?? Options.Create(new CookieTempDataProviderOptions()),
            logger);

        await middleware.InvokeAsync(context);
        return (nextCalled, logger);
    }

    private static (ITempDataProvider Provider, IOptions<CookieTempDataProviderOptions> Options) BuildRealTempDataProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddControllersWithViews();
        var provider = services.BuildServiceProvider();

        return (
            provider.GetRequiredService<ITempDataProvider>(),
            provider.GetRequiredService<IOptions<CookieTempDataProviderOptions>>());
    }

    private static IList<SetCookieHeaderValue> ParseSetCookies(HttpResponse response)
        => SetCookieHeaderValue.ParseList(response.Headers.SetCookie.Where(v => v is not null).ToList()!);

    /// <summary>
    /// Turns a response's Set-Cookie headers (as issued by a real
    /// <see cref="CookieTempDataProvider.SaveTempData"/>) into the Cookie request header a
    /// browser would send back — reused to build a "next request presents this cookie"
    /// context for the round-trip tests.
    /// </summary>
    private static string ToRequestCookieHeader(HttpResponse response)
        => string.Join("; ", ParseSetCookies(response).Select(c => $"{c.Name}={c.Value}"));

    [HumansFact]
    public async Task NoTempDataCookie_PassesThrough_NoLogging()
    {
        var context = new DefaultHttpContext();

        var (nextCalled, logger) = await RunAsync(context);

        nextCalled.Should().BeTrue();
        logger.Entries.Should().BeEmpty();
        context.Response.Headers.SetCookie.Should().BeEmpty();
    }

    [HumansFact]
    public async Task MalformedCookie_LogsExactlyOneWarning_WithRequestContext_NoException()
    {
        const string malformed = "not-valid-base64url!!!";
        var context = new DefaultHttpContext();
        context.Request.Path = "/teams/1234";
        context.Request.Headers.UserAgent = "TestAgent/1.0";
        context.Request.Headers.Cookie = $"{CookieName}={malformed}";

        var (nextCalled, logger) = await RunAsync(context);

        nextCalled.Should().BeTrue("the request must still continue down the pipeline");
        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(Microsoft.Extensions.Logging.LogLevel.Warning);
        entry.Exception.Should().BeNull("no exception object — the log line itself must carry the diagnosis");
        entry.Message.Should().Contain("/teams/1234");
        entry.Message.Should().Contain("TestAgent/1.0");
        entry.Message.Should().Contain(malformed.Length.ToString());
        entry.Message.Should().Contain("False"); // HasChunkSiblings
    }

    [HumansFact]
    public async Task MalformedCookie_IsRemovedFromRequest_AndDeletedOnResponse()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = $"{CookieName}=garbage-not-base64!!!";

        HttpContext? seenByNext = null;
        var logger = new CapturingLogger<TempDataCookieValidationMiddleware>();
        var middleware = new TempDataCookieValidationMiddleware(
            ctx =>
            {
                seenByNext = ctx;
                return Task.CompletedTask;
            },
            Options.Create(new CookieTempDataProviderOptions()),
            logger);

        await middleware.InvokeAsync(context);

        seenByNext.Should().NotBeNull();
        seenByNext!.Request.Cookies.ContainsKey(CookieName).Should()
            .BeFalse("CookieTempDataProvider must never see the poisoned cookie");

        var setCookie = ParseSetCookies(context.Response)
            .Should().ContainSingle(c => string.Equals(c.Name.Value, CookieName, StringComparison.Ordinal)).Subject;
        setCookie.Expires.Should().NotBeNull();
        setCookie.Expires!.Value.Should().BeBefore(DateTimeOffset.UtcNow, "a Delete cookie must be expired");
    }

    [HumansFact]
    public async Task ChunkedCookie_MissingSibling_ReportsHasChunkSiblingsTrue()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = $"{CookieName}=chunks-2; {CookieName}C1=AAAA";
        // C2 deliberately missing — the loss this issue's cause #1 (chunking) describes.

        var (_, logger) = await RunAsync(context);

        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Message.Should().Contain("True"); // HasChunkSiblings
        entry.Exception.Should().BeNull();
    }

    [HumansFact]
    public async Task ValidCookie_IsLeftUntouched_AndTempDataStillRoundTrips()
    {
        var (provider, options) = BuildRealTempDataProvider();

        var saveContext = new DefaultHttpContext();
        provider.SaveTempData(
            saveContext,
            new Dictionary<string, object>(StringComparer.Ordinal) { ["Message"] = "hello world" });

        var loadContext = new DefaultHttpContext();
        loadContext.Request.Headers.Cookie = ToRequestCookieHeader(saveContext.Response);

        var (nextCalled, logger) = await RunAsync(loadContext, options);

        nextCalled.Should().BeTrue();
        logger.Entries.Should().BeEmpty("a valid cookie must not be logged");
        loadContext.Response.Headers.SetCookie.Should().BeEmpty("a valid cookie must not be touched");

        var loaded = provider.LoadTempData(loadContext);
        loaded.Should().ContainKey("Message").WhoseValue.Should().Be("hello world");
    }

    [HumansFact]
    public async Task LargeChunkedCookie_AllChunksPresent_IsValid_AndTempDataStillRoundTrips()
    {
        var (provider, options) = BuildRealTempDataProvider();
        var bigValue = new string('x', 10_000);

        var saveContext = new DefaultHttpContext();
        provider.SaveTempData(
            saveContext,
            new Dictionary<string, object>(StringComparer.Ordinal) { ["Big"] = bigValue });

        var setCookies = ParseSetCookies(saveContext.Response);
        setCookies.Count.Should().BeGreaterThan(1, "a 10,000-char value must actually chunk for this test to be meaningful");

        var loadContext = new DefaultHttpContext();
        loadContext.Request.Headers.Cookie = ToRequestCookieHeader(saveContext.Response);

        var (nextCalled, logger) = await RunAsync(loadContext, options);

        nextCalled.Should().BeTrue();
        logger.Entries.Should().BeEmpty("a fully-present chunked cookie must not be flagged as malformed");
        loadContext.Response.Headers.SetCookie.Should().BeEmpty();

        var loaded = provider.LoadTempData(loadContext);
        loaded.Should().ContainKey("Big").WhoseValue.Should().Be(bigValue);
    }

    [HumansFact]
    public async Task LargeChunkedCookie_MissingChunk_IsFlaggedMalformed_AndTempDataThenLoadsEmpty()
    {
        var (provider, options) = BuildRealTempDataProvider();
        var bigValue = new string('x', 10_000);

        var saveContext = new DefaultHttpContext();
        provider.SaveTempData(
            saveContext,
            new Dictionary<string, object>(StringComparer.Ordinal) { ["Big"] = bigValue });

        var setCookies = ParseSetCookies(saveContext.Response);
        setCookies.Count.Should().BeGreaterThan(2, "need at least 3 pieces so dropping one still leaves siblings");

        // Drop the last chunk cookie to simulate a lost sibling.
        var kept = setCookies.Take(setCookies.Count - 1);
        var loadContext = new DefaultHttpContext();
        loadContext.Request.Headers.Cookie = string.Join("; ", kept.Select(c => $"{c.Name}={c.Value}"));

        var (nextCalled, logger) = await RunAsync(loadContext, options);

        nextCalled.Should().BeTrue();
        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Message.Should().Contain("True"); // HasChunkSiblings
        entry.Exception.Should().BeNull();

        // The middleware already stripped the poisoned cookie from loadContext.Request, so
        // the real provider never sees it and degrades to "no TempData" instead of throwing.
        var loaded = provider.LoadTempData(loadContext);
        loaded.Should().NotContainKey("Big");
    }

    [HumansFact]
    public async Task ChunkedCookie_MissingMiddleChunk_StillDeletesSiblingsAfterTheGap()
    {
        var context = new DefaultHttpContext();
        // C2 lost out of order, C3 still present — a client dropping one cookie rather than
        // truncating from the end. C3 must still be stripped and deleted, or it is re-sent
        // on every request for the rest of the session.
        context.Request.Headers.Cookie =
            $"{CookieName}=chunks-3; {CookieName}C1=AAAA; {CookieName}C3=CCCC";

        HttpContext? seenByNext = null;
        var logger = new CapturingLogger<TempDataCookieValidationMiddleware>();
        var middleware = new TempDataCookieValidationMiddleware(
            ctx =>
            {
                seenByNext = ctx;
                return Task.CompletedTask;
            },
            Options.Create(new CookieTempDataProviderOptions()),
            logger);

        await middleware.InvokeAsync(context);

        seenByNext.Should().NotBeNull();
        seenByNext!.Request.Cookies.ContainsKey($"{CookieName}C3").Should()
            .BeFalse("a sibling after the gap must not survive into the request");

        var deleted = ParseSetCookies(context.Response).Select(c => c.Name.Value).ToArray();
        deleted.Should().Contain([CookieName, $"{CookieName}C1", $"{CookieName}C3"],
            "every present piece is deleted, including the one after the gap");
    }

    [HumansFact]
    public async Task ChunkedCookie_MissingFirstChunk_StillReportsHasChunkSiblingsTrue()
    {
        var context = new DefaultHttpContext();
        // C1 is the lost chunk. Deriving HasChunkSiblings from C1's presence alone reported
        // False here, misclassifying a chunk-loss as a stale-format or bot cookie — the one
        // distinction the diagnostic exists to make.
        context.Request.Headers.Cookie = $"{CookieName}=chunks-2; {CookieName}C2=BBBB";

        var (_, logger) = await RunAsync(context);

        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Message.Should().Contain("HasChunkSiblings=True");
        entry.Exception.Should().BeNull();
    }

    [HumansFact(Timeout = 10_000)]
    public async Task HostileChunkCount_IsBoundedByCookiesActuallySent()
    {
        var context = new DefaultHttpContext();
        // The chunk marker is client-supplied and this middleware runs on every dynamic request.
        // Generating C1..C2147483647 names from it would burn billions of iterations (and wrap
        // the counter), so the declared count must never drive the loop bound. The xUnit timeout
        // is the assertion that matters here — the old shape would not return.
        context.Request.Headers.Cookie = $"{CookieName}=chunks-2147483647";

        var (nextCalled, logger) = await RunAsync(context);

        nextCalled.Should().BeTrue();
        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Message.Should().Contain("HasChunkSiblings=True", "the marker parsed, even though no sibling arrived");
        entry.Exception.Should().BeNull();
    }

    [HumansFact]
    public async Task UnparseableMarker_WithSiblingsPresent_DeletesTheSiblingsToo()
    {
        var context = new DefaultHttpContext();
        // Main value corrupted past carrying a "chunks-N" marker at all, siblings still attached.
        // Deleting only the main cookie would leave multi-KB orphans on the client indefinitely.
        context.Request.Headers.Cookie =
            $"{CookieName}=garbage-not-base64!!!; {CookieName}C1=AAAA; {CookieName}C2=BBBB";

        HttpContext? seenByNext = null;
        var logger = new CapturingLogger<TempDataCookieValidationMiddleware>();
        var middleware = new TempDataCookieValidationMiddleware(
            ctx =>
            {
                seenByNext = ctx;
                return Task.CompletedTask;
            },
            Options.Create(new CookieTempDataProviderOptions()),
            logger);

        await middleware.InvokeAsync(context);

        seenByNext.Should().NotBeNull();
        seenByNext!.Request.Cookies.ContainsKey($"{CookieName}C1").Should().BeFalse();
        seenByNext.Request.Cookies.ContainsKey($"{CookieName}C2").Should().BeFalse();

        var deleted = ParseSetCookies(context.Response).Select(c => c.Name.Value).ToArray();
        deleted.Should().Contain([CookieName, $"{CookieName}C1", $"{CookieName}C2"],
            "siblings are orphans once the main value is unrecoverable");

        logger.Entries.Should().ContainSingle()
            .Which.Message.Should().Contain("HasChunkSiblings=True",
                "no marker survived to parse, so this must come from the siblings actually present");
    }
    [HumansFact]
    public async Task OrphanChunkSiblings_WithoutMainCookie_AreStrippedAndDeleted()
    {
        var context = new DefaultHttpContext();
        // The marker cookie is the one that was lost; its siblings survive. CookieTempDataProvider
        // can never reassemble them, so left alone they are re-sent on every request until expiry.
        context.Request.Headers.Cookie = $"{CookieName}C1=AAAA; {CookieName}C2=BBBB";

        HttpContext? seenByNext = null;
        var logger = new CapturingLogger<TempDataCookieValidationMiddleware>();
        var middleware = new TempDataCookieValidationMiddleware(
            ctx =>
            {
                seenByNext = ctx;
                return Task.CompletedTask;
            },
            Options.Create(new CookieTempDataProviderOptions()),
            logger);

        await middleware.InvokeAsync(context);

        seenByNext.Should().NotBeNull();
        seenByNext!.Request.Cookies.ContainsKey($"{CookieName}C1").Should().BeFalse();
        seenByNext.Request.Cookies.ContainsKey($"{CookieName}C2").Should().BeFalse();

        var deleted = ParseSetCookies(context.Response).Select(c => c.Name.Value).ToArray();
        deleted.Should().Contain([$"{CookieName}C1", $"{CookieName}C2"]);
        deleted.Should().NotContain(CookieName, "the main cookie was never sent, so there is nothing to expire");

        logger.Entries.Should().BeEmpty(
            "no main cookie means no decode and so no FormatException — logging here would only " +
            "add noise to the stream #1038 is diagnosed from");
    }

    [HumansFact]
    public async Task NoncanonicalChunkSuffix_IsNotAcceptedAsAChunk()
    {
        var context = new DefaultHttpContext();
        // "C01" is not the name the framework wrote, and it is the exact name it looks up. Parsing
        // the suffix as index 1 reassembled a valid-looking value here and waved the request
        // through — while CookieTempDataProvider still saw an incomplete chunked cookie and threw.
        context.Request.Headers.Cookie = $"{CookieName}=chunks-1; {CookieName}C01=AAAA";

        var (nextCalled, logger) = await RunAsync(context);

        nextCalled.Should().BeTrue();
        logger.Entries.Should().ContainSingle()
            .Which.Exception.Should().BeNull();
    }
    [HumansFact]
    public async Task DifferentlyCasedCookie_IsDeletedUnderTheNameTheClientSent()
    {
        var context = new DefaultHttpContext();
        // The request collection resolves cookie names case-insensitively, so the framework
        // still finds — and still throws on — this one. A browser matches Set-Cookie
        // case-sensitively though, so expiring the configured casing would leave the real
        // cookie in place and re-log this warning on every subsequent request.
        var sentName = CookieName.ToLowerInvariant();
        context.Request.Headers.Cookie = $"{sentName}=garbage-not-base64!!!";

        HttpContext? seenByNext = null;
        var logger = new CapturingLogger<TempDataCookieValidationMiddleware>();
        var middleware = new TempDataCookieValidationMiddleware(
            ctx =>
            {
                seenByNext = ctx;
                return Task.CompletedTask;
            },
            Options.Create(new CookieTempDataProviderOptions()),
            logger);

        await middleware.InvokeAsync(context);

        seenByNext.Should().NotBeNull();
        seenByNext!.Request.Cookies.ContainsKey(CookieName).Should()
            .BeFalse("the framework looks the cookie up under the configured casing");

        ParseSetCookies(context.Response).Select(c => c.Name.Value)
            .Should().ContainSingle().Which.Should().Be(sentName,
                "only the name the client sent can be expired by the browser");
    }
}
