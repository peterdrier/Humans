using AwesomeAssertions;
using Humans.Web.Middleware;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Humans.Web.Tests.Middleware;

public sealed class CspNonceMiddlewareTests
{
    [HumansFact]
    public async Task ReExecutedRequest_CarriesOneCsp_WithTheNonceThePageRenders()
    {
        // UseStatusCodePagesWithReExecute runs the pipeline again on the same response for a 404/403 page.
        var context = new DefaultHttpContext();
        var middleware = new CspNonceMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context);
        await middleware.InvokeAsync(context);

        var csp = context.Response.Headers.ContentSecurityPolicy;
        csp.Count.Should().Be(1);
        csp.ToString().Should().Contain($"'nonce-{context.Items["CspNonce"]}'");
    }
}
