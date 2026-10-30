using Humans.Camps.Contracts;
using System.Linq.Expressions;
using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base.Authorization;
using Humans.Base.Interfaces;
using Humans.Web.Extensions;
using Humans.Base.ViewComponents;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using NSubstitute;

namespace Humans.Web.Tests.Sections;

/// <summary>
/// Composition order and the generic job-scheduling call: the two places a section's contribution can be silently
/// dropped or mis-shaped.
/// </summary>
public class SectionSeamTests
{
    private sealed class Nav(params AdminNavGroup[] groups) : ISectionAdminNav
    {
        public IEnumerable<AdminNavGroup> Groups() => groups;
    }

    private static AdminNavItem Item(string label, int weight = 0) =>
        new(label, "Some", "Action", null, null, "icon", null, Weight: weight);

    [HumansFact]
    public void Contributing_Nothing_Composes_Nothing()
    {
        AdminNavComposition.Compose([]).Should().BeEmpty();
    }

    [HumansFact]
    public void Contribution_Merges_Into_An_Existing_Group_By_Label()
    {
        var composed = AdminNavComposition.Compose(
        [
            new Nav(new AdminNavGroup("Issues", [Item("Existing")])),
            new Nav(new AdminNavGroup("Issues", [Item("Contributed")]))
        ]);

        composed.Should().ContainSingle();
        composed.Single().Items.Select(i => i.Label).Should().Equal("Existing", "Contributed");
    }

    [HumansFact]
    public void Groups_Order_Alphabetically_Ignoring_Case()
    {
        var composed = AdminNavComposition.Compose(
        [
            new Nav(new AdminNavGroup("Users", [Item("a")])),
            new Nav(new AdminNavGroup("budget", [Item("b")]), new AdminNavGroup("Agent", [Item("c")]))
        ]);

        composed.Select(g => g.Label).Should().Equal("Agent", "budget", "Users");
    }

    /// <summary>The sort is stable, so a group's item order survives another section merging into it.</summary>
    [HumansFact]
    public void Weight_Places_A_Contribution_Around_The_Existing_Items()
    {
        var composed = AdminNavComposition.Compose(
        [
            new Nav(new AdminNavGroup("Cantina", [Item("existing")])),
            new Nav(new AdminNavGroup("Cantina", [Item("second"), Item("first", weight: -1)]))
        ]);

        composed.Single().Items.Select(i => i.Label).Should().Equal("first", "existing", "second");
    }

    private static readonly IReadOnlyList<AdminNavGroup> LocateTree = AdminNavComposition.Compose(
    [
        new Nav(new AdminNavGroup("Barrios", [
            new AdminNavItem("Overview", "CampAdmin", "Index", null, null, "icon", null),
            new AdminNavItem("Roles",    "CampAdmin", "Roles", null, null, "icon", null)
        ])),
        new Nav(new AdminNavGroup("Governance", [
            new AdminNavItem("Voting", "GovernanceBoardVoting", "BoardVoting", null, null, "icon", null)
        ]))
    ]);

    [HumansFact]
    public void Locate_Exact_Route_Is_The_Page_Itself()
    {
        var location = AdminNavComposition.Locate(LocateTree, "campadmin", "roles", parent: null);

        location!.Group.Label.Should().Be("Barrios");
        location.Item.Label.Should().Be("Roles");
        location.IsExact.Should().BeTrue();
    }

    [HumansFact]
    public void Locate_Subpage_Falls_Under_The_First_Item_On_Its_Controller()
    {
        var location = AdminNavComposition.Locate(LocateTree, "CampAdmin", "Detail", parent: null);

        location!.Item.Label.Should().Be("Overview");
        location.IsExact.Should().BeFalse();
    }

    [HumansFact]
    public void Locate_Subpage_Honours_The_Parent_It_Names()
    {
        AdminNavComposition.Locate(LocateTree, "CampAdmin", "RoleForm", parent: "Roles")!
            .Item.Label.Should().Be("Roles");
        AdminNavComposition.Locate(LocateTree, "GovernanceBoardDetail", "Detail", parent: "GovernanceBoardVoting/BoardVoting")!
            .Item.Label.Should().Be("Voting");
    }

    [HumansFact]
    public void Locate_Off_Nav_Controller_Is_Nowhere()
    {
        AdminNavComposition.Locate(LocateTree, "Admin", "Index", parent: null).Should().BeNull();
    }

    private static bool IsAdminPage(string controller, string action, params object[] metadata) =>
        AdminNavComposition.IsAdminPage([new Nav([.. LocateTree])],
            new Endpoint(null, new EndpointMetadataCollection(metadata), null), controller, action);

    private static AuthorizeAttribute Policy(string policy) => new() { Policy = policy };

    [HumansFact]
    public void A_Page_Gated_By_An_Admin_Policy_On_A_Nav_Controller_Or_The_Dashboard_Gets_The_Shell()
    {
        IsAdminPage("CampAdmin", "Roles", Policy(CampsPolicies.Manage)).Should().BeTrue();
        IsAdminPage("CampAdmin", "Detail", Policy(PolicyNames.AppAccess), Policy(CampsPolicies.Manage)).Should().BeTrue();
        IsAdminPage("Admin", "Index", Policy(PolicyNames.AnyAdminRole)).Should().BeTrue();
    }

    /// <summary>A page members share keeps the member layout, even on a controller in the admin nav.</summary>
    [HumansFact]
    public void Shared_Anonymous_And_Off_Nav_Pages_Get_The_Member_Layout()
    {
        IsAdminPage("CampAdmin", "MemberFacingPage", Policy(PolicyNames.AppAccess)).Should().BeFalse();
        IsAdminPage("CampAdmin", "MemberFacingPage", new AuthorizeAttribute()).Should().BeFalse();
        IsAdminPage("CampAdmin", "Roles", Policy(CampsPolicies.Manage), new AllowAnonymousAttribute()).Should().BeFalse();
        IsAdminPage("Profile", "Index", Policy(PolicyNames.AdminOnly)).Should().BeFalse();
    }

    /// <summary>
    /// A class's implicit constructor is public even when the class is internal, so
    /// <c>Activator.CreateInstance(Type)</c> reaches <c>internal sealed</c> contributions.
    /// </summary>
    [HumansFact]
    public void Internal_Section_Contributions_Are_Reflection_Constructible()
    {
        var navs = SectionDiscoveryExtensions.DiscoverImplementations<ISectionAdminNav>();

        navs.Should().NotBeEmpty();
        navs.Should().OnlyContain(
            n => !n.GetType().IsPublic || n is ISection,
            "contributions stay off the section's public surface unless they ride on the Section entry point");
    }

    private interface IReportingJob
    {
        Task<string> ExecuteAsync(CancellationToken cancellationToken);
    }

    private sealed class PlainJob : IRecurringJob
    {
        public Task ExecuteAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [HumansFact]
    public void Job_Call_Is_Built_For_A_Concrete_Job()
    {
        var execute = typeof(PlainJob).GetMethod(nameof(IRecurringJob.ExecuteAsync), [typeof(CancellationToken)])!;

        Expression<Func<PlainJob, Task>> call = RecurringJobExtensions.BuildCall<PlainJob>(execute);

        call.Body.Should().BeAssignableTo<MethodCallExpression>()
            .Which.Method.Name.Should().Be(nameof(IRecurringJob.ExecuteAsync));
    }

    /// <summary>ExecuteAsync returns a report, so the built lambda must still type as <c>Func&lt;T, Task&gt;</c>.</summary>
    [HumansFact]
    public void Job_Call_Is_Built_For_An_Interface_Returning_A_Report()
    {
        var execute = typeof(IReportingJob).GetMethod(nameof(IReportingJob.ExecuteAsync), [typeof(CancellationToken)])!;

        Expression<Func<IReportingJob, Task>> call = RecurringJobExtensions.BuildCall<IReportingJob>(execute);

        call.ReturnType.Should().Be(typeof(Task));
    }

    private sealed class NotAJob;

    /// <summary>
    /// The roll-call is built outside the per-job try/catch, so a type with no ExecuteAsync must not throw there: it
    /// would stop the app booting.
    /// </summary>
    [HumansFact]
    public void Malformed_Job_Descriptor_Fails_At_Schedule_Time_Not_While_Listing()
    {
        var listing = () => RecurringJobExtensions.ToScheduledJob(
            new RecurringJobDescriptor("bad-job", typeof(NotAJob), "* * * * *"));

        listing.Should().NotThrow();
        listing().Schedule.Should().Throw<InvalidOperationException>();
    }

    private sealed class MemberNav(params MemberNavItem[] items) : ISectionNav
    {
        public IEnumerable<MemberNavItem> Items() => items;
    }

    [HumansFact]
    public async Task Dropdown_Children_Are_Gated_Like_Top_Level_Items()
    {
        var model = await ComposeNavAsync(new MemberNavItem("Parent", Children:
        [
            new MemberNavItem("Shown", Policy: "allowed"),
            new MemberNavItem("Denied", Policy: "denied"),
            new MemberNavItem("Invisible", Visible: (_, _) => false)
        ]));

        model.Single().Children!.Select(c => c.Label).Should().Equal("Shown");
    }

    /// <summary>
    /// Equal weights keep declared order rather than sorting on the label, as in <see cref="AdminNavComposition"/>;
    /// neither has a tie-break.
    /// </summary>
    [HumansFact]
    public async Task Top_Level_Items_Order_By_Weight_And_Keep_Declared_Order()
    {
        var model = await ComposeNavAsync(
            new MemberNavItem("zulu"),
            new MemberNavItem("alpha"),
            new MemberNavItem("first", Weight: -1));

        model.Select(i => i.Label).Should().Equal("first", "zulu", "alpha");
    }

    [HumansFact]
    public async Task Dropdown_Children_Order_By_Weight()
    {
        var model = await ComposeNavAsync(new MemberNavItem("Parent", Children:
        [
            new MemberNavItem("last", Weight: 10),
            new MemberNavItem("first", Weight: -1),
            new MemberNavItem("middle-a"),
            new MemberNavItem("middle-b")
        ]));

        model.Single().Children!.Select(c => c.Label)
            .Should().Equal("first", "middle-a", "middle-b", "last");
    }

    [HumansFact]
    public async Task Dropdown_With_No_Visible_Children_Is_Dropped()
    {
        var model = await ComposeNavAsync(new MemberNavItem("Parent", Children:
            [new MemberNavItem("Denied", Policy: "denied")]));

        model.Should().BeEmpty();
    }

    private static async Task<IReadOnlyList<MemberNavItem>> ComposeNavAsync(params MemberNavItem[] items)
    {
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(call => Task.FromResult(string.Equals((string)call[2], "allowed", StringComparison.Ordinal)
                ? AuthorizationResult.Success()
                : AuthorizationResult.Failed()));

        var sut = new SectionNavViewComponent(
            [new MemberNav(items)], authorization, Substitute.For<IServiceProvider>())
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext
                {
                    HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) }
                }
            }
        };

        var result = await sut.InvokeAsync() as ViewViewComponentResult;
        return (IReadOnlyList<MemberNavItem>)result!.ViewData!.Model!;
    }

    /// <summary>
    /// Pinned because declaring a non-public or parameterised constructor on a contribution would break section
    /// discovery at startup: <c>Activator.CreateInstance(Type)</c> finds only public constructors.
    /// </summary>
    [HumansFact]
    public void Internal_Contribution_With_An_Implicit_Constructor_Activates()
    {
        typeof(InternalNavContribution).IsPublic.Should().BeFalse();
        typeof(InternalNavContribution).GetConstructor(Type.EmptyTypes)!.IsPublic.Should().BeTrue();

        var activated = (ISectionNav)Activator.CreateInstance(typeof(InternalNavContribution))!;

        activated.Items().Should().ContainSingle();
    }
}

/// <summary>Top-level so its accessibility is real, not a nested type's.</summary>
internal sealed class InternalNavContribution : ISectionNav
{
    public IEnumerable<MemberNavItem> Items() => [new MemberNavItem("Contributed")];
}
