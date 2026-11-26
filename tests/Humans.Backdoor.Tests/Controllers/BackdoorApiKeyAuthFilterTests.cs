using AwesomeAssertions;
using Humans.Auth.Contracts;
using Humans.Backdoor.Filters;
using Humans.Backdoor.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using NSubstitute;
using System.Reflection;
using System.Security.Claims;

namespace Humans.Backdoor.Tests.Controllers;

/// <summary>
/// The one gate on <c>/api/backdoor/*</c>. It resolves the key to a person rather than comparing it
/// to an env var, so the interesting assertion is the principal it installs, not just the status code.
/// </summary>
public class BackdoorApiKeyAuthFilterTests
{
    private readonly IBackdoorApiKeyService _keys = Substitute.For<IBackdoorApiKeyService>();

    public BackdoorApiKeyAuthFilterTests()
    {
        _keys.AdmitsAsync(Arg.Any<BackdoorKeyOwner>(), BackdoorScope.AdminAndBoard, Arg.Any<CancellationToken>())
            .Returns(true);
    }

    [HumansFact]
    public void Every_machine_controller_hangs_off_this_filter()
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
        var owner = new BackdoorKeyOwner(Guid.NewGuid(), []);
        _keys.ResolveOwnerAsync("right-key", Arg.Any<CancellationToken>()).Returns(owner);
        _keys.AdmitsAsync(owner, BackdoorScope.AdminAndBoard, Arg.Any<CancellationToken>()).Returns(false);
        var ctx = MakeContext(headerKey: "right-key");

        await Filter().OnAuthorizationAsync(ctx);

        ctx.Result.Should().BeOfType<StatusCodeResult>().Which.StatusCode.Should().Be(403);
    }

    [HumansFact]
    public async Task A_controller_declaring_no_scope_serves_nobody()
    {
        _keys.ResolveOwnerAsync("right-key", Arg.Any<CancellationToken>())
            .Returns(new BackdoorKeyOwner(Guid.NewGuid(), []));
        var ctx = MakeContext(headerKey: "right-key", scope: null);

        await Filter().OnAuthorizationAsync(ctx);

        ctx.Result.Should().BeOfType<StatusCodeResult>().Which.StatusCode.Should().Be(403);
    }

    [HumansFact]
    public async Task Missing_header_is_401()
    {
        var ctx = MakeContext(headerKey: null);

        await Filter().OnAuthorizationAsync(ctx);

        ctx.Result.Should().BeOfType<UnauthorizedResult>();
        await _keys.DidNotReceiveWithAnyArgs().ResolveOwnerAsync(default!);
    }

    [HumansFact]
    public async Task Unresolvable_key_is_401()
    {
        // Keys are rows, not deploy-time config, so an empty table is an unauthorized caller, not a broken server.
        _keys.ResolveOwnerAsync("wrong-key", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<BackdoorKeyOwner?>(null));
        var ctx = MakeContext(headerKey: "wrong-key");

        await Filter().OnAuthorizationAsync(ctx);

        ctx.Result.Should().BeOfType<UnauthorizedResult>();
    }

    [HumansFact]
    public async Task Valid_key_passes_through_as_its_owner()
    {
        var ownerId = Guid.NewGuid();
        _keys.ResolveOwnerAsync("right-key", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<BackdoorKeyOwner?>(new BackdoorKeyOwner(ownerId, [])));
        var ctx = MakeContext(headerKey: "right-key");

        await Filter().OnAuthorizationAsync(ctx);

        ctx.Result.Should().BeNull();
        ctx.HttpContext.User.FindFirstValue(ClaimTypes.NameIdentifier).Should().Be(ownerId.ToString());
        ctx.HttpContext.User.Identity!.AuthenticationType
            .Should().Be(BackdoorApiKeyAuthFilter.AuthenticationScheme);
    }

    [HumansFact]
    public async Task Owners_active_roles_ride_along_on_the_principal()
    {
        // The queues this key opens scope themselves off User.IsInRole and the role claims,
        // so a key that arrived role-less read every one of them as a full admin.
        var ownerId = Guid.NewGuid();
        _keys.ResolveOwnerAsync("right-key", Arg.Any<CancellationToken>())
            .Returns(new BackdoorKeyOwner(ownerId,
                [new SystemRoleSnapshot("Board", null), new SystemRoleSnapshot("Admin", null)]));
        var ctx = MakeContext(headerKey: "right-key");

        await Filter().OnAuthorizationAsync(ctx);

        ctx.HttpContext.User.FindAll(ClaimTypes.Role).Select(c => c.Value)
            .Should().BeEquivalentTo(["Board", "Admin"]);
        ctx.HttpContext.User.IsInRole("Admin").Should().BeTrue();
    }

    [HumansFact]
    public async Task An_owner_with_no_active_roles_gets_no_role_claims()
    {
        var ownerId = Guid.NewGuid();
        _keys.ResolveOwnerAsync("right-key", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<BackdoorKeyOwner?>(new BackdoorKeyOwner(ownerId, [])));
        var ctx = MakeContext(headerKey: "right-key");

        await Filter().OnAuthorizationAsync(ctx);

        ctx.HttpContext.User.FindAll(ClaimTypes.Role).Should().BeEmpty();
        ctx.HttpContext.User.IsInRole("Admin").Should().BeFalse();
    }

    private BackdoorApiKeyAuthFilter Filter() => new(_keys);

    private static AuthorizationFilterContext MakeContext(
        string? headerKey, BackdoorScope? scope = BackdoorScope.AdminAndBoard)
    {
        var http = new DefaultHttpContext();
        if (headerKey is not null)
            http.Request.Headers[BackdoorApiKeyAuthFilter.ApiKeyHeaderName] = headerKey;

        return new AuthorizationFilterContext(
            new ActionContext(http, new RouteData(), new ActionDescriptor
            {
                EndpointMetadata = scope is { } s ? [new BackdoorScopeAttribute(s)] : [],
            }),
            []);
    }
}
