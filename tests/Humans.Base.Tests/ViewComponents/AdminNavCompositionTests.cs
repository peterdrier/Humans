using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base.Authorization;
using Humans.Base.Interfaces;
using Humans.Base.ViewComponents;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NSubstitute;

namespace Humans.Base.Tests.ViewComponents;

public class AdminNavCompositionTests
{
    private static readonly IReadOnlyList<AdminNavGroup> Groups =
    [
        new("Debug", [
            new("Logs",     "Debug", "Logs",    null, null, "icon", null),
            new("DB stats", "Debug", "DbStats", null, null, "icon", null)
        ])
    ];

    [HumansFact]
    public void Locate_SharedController_MatchesByAction()
    {
        // Regression: DebugController has several items; a controller-only match returned the first.
        AdminNavComposition.Locate(Groups, "Debug", "DbStats", parent: null)!.Item.Label.Should().Be("DB stats");
    }

    [HumansFact]
    public void Locate_NamedParent_WinsOverFirstItem()
    {
        var location = AdminNavComposition.Locate(Groups, "Debug", "LogDetail", parent: "DbStats")!;

        location.Item.Label.Should().Be("DB stats");
        location.IsExact.Should().BeFalse();
    }

    private sealed class Nav(params AdminNavGroup[] groups) : ISectionAdminNav
    {
        public IEnumerable<AdminNavGroup> Groups() => groups;
    }

    /// <summary>A page members share, behind AppAccess only, listed as an admin nav item.</summary>
    private static Task<bool> IsShellPageAsync(string itemPolicy, bool authorized)
    {
        var nav = new Nav(new AdminNavGroup("Issues", [new("Issues", "Issues", "Index", null, null, "icon", itemPolicy)]));
        var endpoint = new Endpoint(null, new EndpointMetadataCollection(new AuthorizeAttribute { Policy = PolicyNames.AppAccess }), null);
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(authorized ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        var route = new RouteValueDictionary { ["controller"] = "Issues", ["action"] = "Index" };
        return AdminNavComposition.IsShellPageAsync([nav], endpoint, route, authorization, new ClaimsPrincipal());
    }

    [HumansFact]
    public async Task IsShellPageAsync_SharedNavItemAuthorized_GetsShell()
    {
        (await IsShellPageAsync(PolicyNames.AdminOnly, authorized: true)).Should().BeTrue();
    }

    [HumansFact]
    public async Task IsShellPageAsync_SharedNavItemUnauthorized_GetsMemberLayout()
    {
        (await IsShellPageAsync(PolicyNames.AdminOnly, authorized: false)).Should().BeFalse();
    }

    [HumansFact]
    public async Task IsShellPageAsync_AppAccessItem_GetsMemberLayout()
    {
        (await IsShellPageAsync(PolicyNames.AppAccess, authorized: true)).Should().BeFalse();
    }
}
