using AwesomeAssertions;
using Humans.Base.Interfaces;
using Humans.Preapprovals.Authorization;
using Humans.Preapprovals.Controllers;

namespace Humans.Preapprovals.Tests.Controllers;

public sealed class SectionNavTests
{
    [HumansFact]
    public void AdminSidebar_PointsAtTheManageIndex_GatedByManage()
    {
        ISectionAdminNav navigation = new Section();
        var item = navigation.Groups().SelectMany(g => g.Items).Single();
        item.Controller.Should().Be(nameof(PreapprovalsAdminController).Replace("Controller", "", StringComparison.Ordinal));
        item.Action.Should().Be(nameof(PreapprovalsAdminController.Index));
        item.Policy.Should().Be(PreapprovalsPolicies.Manage);
    }

    [HumansFact]
    public void MemberNav_MineUnderAccess_ManageUnderManage()
    {
        ISectionNav navigation = new SectionNav();
        var top = navigation.Items().Single();
        top.Policy.Should().Be(PreapprovalsPolicies.Access);
        top.Children.Should().NotBeNull();
        top.Children!.Where(c => !c.Divider).Select(c => (c.Controller, c.Policy)).Should().Equal(
            ("Preapprovals", PreapprovalsPolicies.Access),
            ("PreapprovalsAdmin", PreapprovalsPolicies.Manage));
    }
}
