using System.Security.Claims;
using AwesomeAssertions;
using Humans.Auth.Contracts;
using Humans.Notifications.Filters;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Notifications.Tests.Controllers;

public class NotificationApiKeyAuthFilterTests
{
    private readonly IUserServiceRead _users = Substitute.For<IUserServiceRead>();
    private readonly IRoleAssignmentService _roles = Substitute.For<IRoleAssignmentService>();

    [HumansFact]
    public async Task Missing_key_is_401()
    {
        var context = MakeContext();

        await Filter("right-key", Guid.NewGuid().ToString()).OnAuthorizationAsync(context);

        context.Result.Should().BeOfType<UnauthorizedResult>();
        await _users.DidNotReceiveWithAnyArgs().GetUserInfoAsync(default, default);
    }

    [HumansFact]
    public async Task Wrong_key_is_401()
    {
        var context = MakeContext("wrong-key");

        await Filter("right-key", Guid.NewGuid().ToString()).OnAuthorizationAsync(context);

        context.Result.Should().BeOfType<UnauthorizedResult>();
        await _users.DidNotReceiveWithAnyArgs().GetUserInfoAsync(default, default);
    }

    [HumansFact]
    public async Task Unset_configured_key_is_401()
    {
        var context = MakeContext("presented-key");

        await Filter(string.Empty, Guid.NewGuid().ToString()).OnAuthorizationAsync(context);

        context.Result.Should().BeOfType<UnauthorizedResult>();
        await _users.DidNotReceiveWithAnyArgs().GetUserInfoAsync(default, default);
    }

    [HumansTheory]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Unset_or_invalid_user_id_is_401(string configuredUserId)
    {
        var context = MakeContext("right-key");

        await Filter("right-key", configuredUserId).OnAuthorizationAsync(context);

        context.Result.Should().BeOfType<UnauthorizedResult>();
        await _users.DidNotReceiveWithAnyArgs().GetUserInfoAsync(default, default);
    }

    [HumansFact]
    public async Task Missing_configured_user_is_401()
    {
        var userId = Guid.NewGuid();
        _users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult<UserInfo?>(null));
        var context = MakeContext("right-key");

        await Filter("right-key", userId.ToString()).OnAuthorizationAsync(context);

        context.Result.Should().BeOfType<UnauthorizedResult>();
        await _roles.DidNotReceiveWithAnyArgs().GetActiveForUserAsync(default, default);
    }

    [HumansFact]
    public async Task Tombstoned_configured_user_is_401()
    {
        var userId = Guid.NewGuid();
        _users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult<UserInfo?>(MakeUser(
                userId, UserInfo.GdprAnonymizedBurnerName)));
        var context = MakeContext("right-key");

        await Filter("right-key", userId.ToString()).OnAuthorizationAsync(context);

        context.Result.Should().BeOfType<UnauthorizedResult>();
        await _roles.DidNotReceiveWithAnyArgs().GetActiveForUserAsync(default, default);
    }

    [HumansFact]
    public async Task Valid_mapping_installs_user_and_active_roles()
    {
        var userId = Guid.NewGuid();
        _users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult<UserInfo?>(MakeUser(userId)));
        _roles.GetActiveForUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<RoleAssignmentSnapshot>>(
                [new("Admin", null)]));
        var context = MakeContext("right-key");

        await Filter("right-key", userId.ToString()).OnAuthorizationAsync(context);

        context.Result.Should().BeNull();
        context.HttpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
            .Should().Be(userId.ToString());
        context.HttpContext.User.IsInRole("Admin").Should().BeTrue();
    }

    [HumansFact]
    public async Task Valid_mapping_replaces_cookie_user_and_roles()
    {
        var configuredUserId = Guid.NewGuid();
        var cookieUserId = Guid.NewGuid();
        _users.GetUserInfoAsync(configuredUserId, Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult<UserInfo?>(MakeUser(configuredUserId)));
        _roles.GetActiveForUserAsync(configuredUserId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<RoleAssignmentSnapshot>>(
                [new("Admin", null)]));
        var cookiePrincipal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, cookieUserId.ToString()),
                new Claim(ClaimTypes.Role, "Board"),
            ], "Cookies"));
        var context = MakeContext("right-key", cookiePrincipal);

        await Filter("right-key", configuredUserId.ToString()).OnAuthorizationAsync(context);

        context.Result.Should().BeNull();
        context.HttpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
            .Should().Be(configuredUserId.ToString());
        context.HttpContext.User.IsInRole("Admin").Should().BeTrue();
        context.HttpContext.User.IsInRole("Board").Should().BeFalse();
        context.HttpContext.User.Identity!.AuthenticationType
            .Should().Be(NotificationApiKeyAuthFilter.AuthenticationScheme);
    }

    private NotificationApiKeyAuthFilter Filter(string apiKey, string userId) =>
        new(Options.Create(new NotificationApiOptions
        {
            ApiKey = apiKey,
            UserId = userId,
        }), _users, _roles);

    private static AuthorizationFilterContext MakeContext(
        string? apiKey = null, ClaimsPrincipal? principal = null)
    {
        var http = new DefaultHttpContext
        {
            User = principal ?? new ClaimsPrincipal(),
        };
        if (apiKey is not null)
            http.Request.Headers[NotificationApiKeyAuthFilter.ApiKeyHeaderName] = apiKey;

        return new AuthorizationFilterContext(
            new ActionContext(http, new RouteData(), new ActionDescriptor()), []);
    }

    private static UserInfo MakeUser(Guid userId, string displayName = "API user") => UserInfo.Create(
        new User
        {
            Id = userId,
            DisplayName = displayName,
            PreferredLanguage = "en",
            CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
        }, [], [], [], profile: null, []);
}
