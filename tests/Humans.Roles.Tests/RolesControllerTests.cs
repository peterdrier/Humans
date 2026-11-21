using AwesomeAssertions;
using Humans.Base.Authorization;
using Humans.Roles.Contracts;
using Humans.Roles.Controllers;
using Humans.Roles.Services;
using Microsoft.AspNetCore.Authorization;
using Xunit;

namespace Humans.Roles.Tests;

public sealed class RolesControllerTests
{
    [HumansTheory]
    [InlineData(false, "Production › Cantina")]
    [InlineData(true, "Cantina")]
    public void OwnerLabel_LeavesOutAHiddenParent(bool parentHidden, string expected)
    {
        var parent = new OwnerView(new Owner("Team", Guid.NewGuid()), "production", "Production", "/", parentHidden, true, null);
        var child = new OwnerView(new Owner("Team", Guid.NewGuid()), "cantina", "Cantina", "/", false, true, parent.Owner.Id);

        RolesController.OwnerLabel(child, [parent, child]).Should().Be(expected);
    }

    [HumansFact]
    public void Admin_IsOnlyForAdminAndRolesAdmin() =>
        typeof(RolesController).GetMethod(nameof(RolesController.Admin))!
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false).Cast<AuthorizeAttribute>()
            .Should().ContainSingle(a => a.Policy == RolesPolicies.Admin);
}
