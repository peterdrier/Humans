using AwesomeAssertions;
using Humans.Base.Authorization;
using Humans.Base.Interfaces;
using Humans.Workgroups.Controllers;

namespace Humans.Workgroups.Tests.Controllers;

public sealed class SectionAdminNavTests
{
    [HumansFact]
    public void AdminSidebar_AlwaysContributesTheAuthorizedSetupDestination()
    {
        ISectionAdminNav navigation = new Section();

        var item = navigation.Groups().SelectMany(g => g.Items).Single();

        item.Controller.Should().Be(nameof(WorkgroupsAdminController).Replace("Controller", "", StringComparison.Ordinal));
        item.Action.Should().Be(nameof(WorkgroupsAdminController.Index));
        item.Policy.Should().Be(PolicyNames.BoardOrAdmin);
    }
}
