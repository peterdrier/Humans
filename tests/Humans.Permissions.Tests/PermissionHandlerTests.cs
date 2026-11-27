using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base.Authorization;
using Humans.Permissions.Authorization;
using Humans.Permissions.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using Humans.Testing;
using Xunit;

namespace Humans.Permissions.Tests;

public sealed class PermissionHandlerTests
{
    private static readonly Permission TeamsManage = new(
        "Teams.Manage", "Manage the team", "", ["Team"], TopLevelOnly: false, AdminRoles: [], typeof(PermissionHandlerTests));

    private readonly IPermissionService _permissions = Substitute.For<IPermissionService>();
    private readonly IUserService _users = Substitute.For<IUserService>();

    [HumansTheory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Handle_MergedCookieId_ChecksSurvivor(bool survivorExists, bool expected)
    {
        var cookieId = Guid.NewGuid();
        var survivorId = Guid.NewGuid();
        var owner = new Owner("Team", Guid.NewGuid());
        _users.GetUserInfoAsync(cookieId, Arg.Any<CancellationToken>()).Returns(survivorExists
            ? UserFixtures.MakeUserInfo(survivorId)
            : null);
        _permissions.HasAsync(survivorId, TeamsManage.Key, owner, Arg.Any<CancellationToken>()).Returns(true);
        var requirement = new PermissionRequirement(TeamsManage);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, cookieId.ToString())], "test"));
        var context = new AuthorizationHandlerContext([requirement], principal, owner);

        await new PermissionHandler(_permissions, _users, new HttpContextAccessor()).HandleAsync(context);

        context.HasSucceeded.Should().Be(expected);
    }
}
