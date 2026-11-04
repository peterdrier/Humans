using System.Security.Claims;
using Humans.CityPlanning.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.SignalR;
using NSubstitute;
using Xunit;

namespace Humans.CityPlanning.Tests;

public sealed class CityPlanningHubTests
{
    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly IClientProxy _others = Substitute.For<IClientProxy>();

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cursor_UsesTheResolvedBurnerName(bool merged)
    {
        var claimId = Guid.NewGuid();
        var resolvedId = merged ? Guid.NewGuid() : claimId;
        _users.GetUserInfoAsync(claimId).Returns(
            UserInfo.Create(new User { Id = resolvedId, BurnerName = "Map Human" }, [], [], null));
        var hub = CreateHub(claimId.ToString());

        await hub.OnConnectedAsync();
        await hub.UpdateCursorAsync(1, 2);

        await ExpectCursorAsync(hub, "Map Human");
        await hub.OnDisconnectedAsync(null);
    }

    [HumansTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    public async Task InvalidIdentifier_UsesSessionNameWithoutReadingUsers(string? identifier)
    {
        var hub = CreateHub(identifier);

        await hub.OnConnectedAsync();
        await hub.UpdateCursorAsync(1, 2);

        await _users.DidNotReceiveWithAnyArgs().GetUserInfoAsync(default, default);
        await ExpectCursorAsync(hub, "Session Human");
        await hub.OnDisconnectedAsync(null);
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingUserOrBurnerName_UsesSessionName(bool exists)
    {
        var claimId = Guid.NewGuid();
        _users.GetUserInfoAsync(claimId).Returns(exists
            ? UserInfo.Create(new User { Id = claimId, BurnerName = " " }, [], [], null)
            : null);
        var hub = CreateHub(claimId.ToString());

        await hub.OnConnectedAsync();
        await hub.UpdateCursorAsync(1, 2);

        await ExpectCursorAsync(hub, "Session Human");
        await hub.OnDisconnectedAsync(null);
    }

    private CityPlanningHub CreateHub(string? identifier)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, "Session Human") };
        if (identifier is not null) claims.Add(new Claim(ClaimTypes.NameIdentifier, identifier));
        var context = Substitute.For<HubCallerContext>();
        context.User.Returns(new ClaimsPrincipal(new ClaimsIdentity(claims, "test")));
        context.ConnectionId.Returns(Guid.NewGuid().ToString());
        var clients = Substitute.For<IHubCallerClients>();
        clients.Others.Returns(_others);
        return new CityPlanningHub(_users) { Context = context, Clients = clients };
    }

    private Task ExpectCursorAsync(CityPlanningHub hub, string name) =>
        _others.Received(1).SendCoreAsync("CursorMoved",
            Arg.Is<object?[]>(a => (string)a[0]! == hub.Context.ConnectionId
                && (string)a[1]! == name && (double)a[2]! == 1 && (double)a[3]! == 2),
            Arg.Any<CancellationToken>());
}
