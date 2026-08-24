using AwesomeAssertions;
using Humans.Backdoor.Filters;
using Humans.Backdoor.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using NSubstitute;
using System.Security.Claims;

namespace Humans.Backdoor.Tests.Controllers;

/// <summary>
/// The one gate on <c>/api/backdoor/*</c>. Unlike the five per-section filters it replaced,
/// it resolves the key to a person rather than comparing it to an env var — so the
/// interesting assertion is the principal it installs, not just the status code
/// (nobodies-collective/Humans#1128).
/// </summary>
public class BackdoorApiKeyAuthFilterTests
{
    private readonly IBackdoorApiKeyService _keys = Substitute.For<IBackdoorApiKeyService>();

    [HumansFact]
    public async Task Missing_header_is_401()
    {
        var ctx = MakeContext(headerKey: null);

        await new BackdoorApiKeyAuthFilter(_keys).OnAuthorizationAsync(ctx);

        ctx.Result.Should().BeOfType<UnauthorizedResult>();
        await _keys.DidNotReceiveWithAnyArgs().ResolveOwnerAsync(default!, default);
    }

    [HumansFact]
    public async Task Unknown_or_revoked_key_is_401()
    {
        // No 503 "not configured" branch any more: keys are rows, not deploy-time config,
        // so an empty table is an unauthorized caller, not a broken server.
        _keys.ResolveOwnerAsync("wrong-key", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Guid?>(null));
        var ctx = MakeContext(headerKey: "wrong-key");

        await new BackdoorApiKeyAuthFilter(_keys).OnAuthorizationAsync(ctx);

        ctx.Result.Should().BeOfType<UnauthorizedResult>();
    }

    [HumansFact]
    public async Task Valid_key_passes_through_as_its_owner()
    {
        var ownerId = Guid.NewGuid();
        _keys.ResolveOwnerAsync("right-key", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Guid?>(ownerId));
        var ctx = MakeContext(headerKey: "right-key");

        await new BackdoorApiKeyAuthFilter(_keys).OnAuthorizationAsync(ctx);

        ctx.Result.Should().BeNull();
        ctx.HttpContext.User.FindFirstValue(ClaimTypes.NameIdentifier).Should().Be(ownerId.ToString());
        ctx.HttpContext.User.Identity!.AuthenticationType
            .Should().Be(BackdoorApiKeyAuthFilter.AuthenticationScheme);
    }

    private static AuthorizationFilterContext MakeContext(string? headerKey)
    {
        var http = new DefaultHttpContext();
        if (headerKey is not null)
            http.Request.Headers[BackdoorApiKeyAuthFilter.ApiKeyHeaderName] = headerKey;

        return new AuthorizationFilterContext(
            new ActionContext(http, new RouteData(), new ActionDescriptor()),
            []);
    }
}
