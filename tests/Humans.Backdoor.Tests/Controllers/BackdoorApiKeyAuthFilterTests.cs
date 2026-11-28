using AwesomeAssertions;
using Humans.Backdoor.Authentication;
using Humans.Backdoor.Filters;
using Humans.Backdoor.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using System.Reflection;
using System.Security.Claims;

namespace Humans.Backdoor.Tests.Controllers;

/// <summary>
/// The one gate on <c>/api/backdoor/*</c>, run against the real key scheme so the principal it installs is the one
/// <see cref="BackdoorApiKeyHandler"/> builds.
/// </summary>
public class BackdoorApiKeyAuthFilterTests
{
    private readonly IBackdoorApiKeyService _keys = Substitute.For<IBackdoorApiKeyService>();

    public BackdoorApiKeyAuthFilterTests()
    {
        _keys.AdmitsAsync(Arg.Any<Guid>(), BackdoorScope.AdminAndBoard, Arg.Any<CancellationToken>())
            .Returns(true);
    }

    [HumansFact]
    public void MachineControllers_Each_UsesThisFilter()
    {
        var machineControllers = typeof(BackdoorApiKeyAuthFilter).Assembly.GetTypes()
            .Where(t => t.GetCustomAttributes<RouteAttribute>(inherit: false)
                .Any(r => r.Template.StartsWith("api/backdoor", StringComparison.Ordinal)))
            .ToList();

        machineControllers.Should().NotBeEmpty();
        machineControllers.Should().AllSatisfy(t => t.GetCustomAttributes<ServiceFilterAttribute>(inherit: false)
            .Should().ContainSingle(f => f.ServiceType == typeof(BackdoorApiKeyAuthFilter), t.Name));
        machineControllers.Should().AllSatisfy(t =>
            t.GetCustomAttribute<BackdoorScopeAttribute>().Should().NotBeNull(t.Name));
    }

    [HumansFact]
    public async Task OnAuthorization_OwnerOutsideScope_Returns403()
    {
        var ownerId = Guid.NewGuid();
        _keys.ResolveOwnerAsync("right-key", Arg.Any<CancellationToken>()).Returns(new BackdoorKeyOwner(ownerId, []));
        _keys.AdmitsAsync(ownerId, BackdoorScope.AdminAndBoard, Arg.Any<CancellationToken>()).Returns(false);
        var ctx = MakeContext(headerKey: "right-key");

        await Filter().OnAuthorizationAsync(ctx);

        ctx.Result.Should().BeOfType<StatusCodeResult>().Which.StatusCode.Should().Be(403);
    }

    [HumansFact]
    public async Task OnAuthorization_NoScopeDeclared_Returns403()
    {
        _keys.ResolveOwnerAsync("right-key", Arg.Any<CancellationToken>())
            .Returns(new BackdoorKeyOwner(Guid.NewGuid(), []));
        var ctx = MakeContext(headerKey: "right-key", scope: null);

        await Filter().OnAuthorizationAsync(ctx);

        ctx.Result.Should().BeOfType<StatusCodeResult>().Which.StatusCode.Should().Be(403);
    }

    [HumansFact]
    public async Task OnAuthorization_MissingHeader_Returns401()
    {
        var ctx = MakeContext(headerKey: null);

        await Filter().OnAuthorizationAsync(ctx);

        ctx.Result.Should().BeOfType<UnauthorizedResult>();
        await _keys.DidNotReceiveWithAnyArgs().ResolveOwnerAsync(default!);
    }

    [HumansFact]
    public async Task OnAuthorization_UnresolvableKey_Returns401()
    {
        // Keys are rows, not deploy-time config, so an empty table is an unauthorized caller, not a broken server.
        _keys.ResolveOwnerAsync("wrong-key", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<BackdoorKeyOwner?>(null));
        var ctx = MakeContext(headerKey: "wrong-key");

        await Filter().OnAuthorizationAsync(ctx);

        ctx.Result.Should().BeOfType<UnauthorizedResult>();
    }

    [HumansFact]
    public async Task OnAuthorization_ValidKey_InstallsOwnerAsPrincipal()
    {
        var ownerId = Guid.NewGuid();
        _keys.ResolveOwnerAsync("right-key", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<BackdoorKeyOwner?>(new BackdoorKeyOwner(ownerId, [])));
        var ctx = MakeContext(headerKey: "right-key");

        await Filter().OnAuthorizationAsync(ctx);

        ctx.Result.Should().BeNull();
        ctx.HttpContext.User.FindFirstValue(ClaimTypes.NameIdentifier).Should().Be(ownerId.ToString());
        ctx.HttpContext.User.Identity!.AuthenticationType.Should().Be(BackdoorApiKeyHandler.SchemeName);
    }

    private BackdoorApiKeyAuthFilter Filter() => new(_keys);

    private AuthorizationFilterContext MakeContext(
        string? headerKey, BackdoorScope? scope = BackdoorScope.AdminAndBoard)
    {
        var services = new ServiceCollection().AddLogging().AddSingleton(_keys);
        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, BackdoorApiKeyHandler>(BackdoorApiKeyHandler.SchemeName, null);
        var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        if (headerKey is not null)
            http.Request.Headers[BackdoorApiKeyHandler.HeaderName] = headerKey;

        return new AuthorizationFilterContext(
            new ActionContext(http, new RouteData(), new ActionDescriptor
            {
                EndpointMetadata = scope is { } s ? [new BackdoorScopeAttribute(s)] : [],
            }),
            []);
    }
}
