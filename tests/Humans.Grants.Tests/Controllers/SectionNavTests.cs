using AwesomeAssertions;
using Humans.Base.Interfaces;
using Humans.Grants.Authorization;
using Humans.Grants.Controllers;

namespace Humans.Grants.Tests.Controllers;

public sealed class SectionNavTests
{
    [HumansFact]
    public void AdminSidebar_PointsAtTheManageIndex_GatedByManage()
    {
        ISectionAdminNav navigation = new Section();
        var item = navigation.Groups().SelectMany(g => g.Items).Single();
        item.Controller.Should().Be(nameof(GrantsAdminController).Replace("Controller", "", StringComparison.Ordinal));
        item.Action.Should().Be(nameof(GrantsAdminController.Index));
        item.Policy.Should().Be(GrantsPolicies.Manage);
    }
}
