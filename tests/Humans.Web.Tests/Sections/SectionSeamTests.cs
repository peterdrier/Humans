using System.Linq.Expressions;
using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base.Interfaces;
using Humans.Web.Extensions;
using Humans.Web.ViewComponents;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using NSubstitute;

namespace Humans.Web.Tests.Sections;

/// <summary>
/// The contribution seams (nobodies-collective/Humans#1073): composition order and the
/// generic job-scheduling call, which are the two places a section's contribution can be
/// silently dropped or mis-shaped.
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
    public void Contributing_Nothing_Leaves_The_Tree_Untouched()
    {
        AdminNavComposition.Compose([]).Should().BeSameAs(AdminNavTree.Groups);
    }

    [HumansFact]
    public void Contribution_Merges_Into_An_Existing_Group_By_Key()
    {
        var existing = AdminNavTree.Groups.First(g => string.Equals(g.GroupKey, "Tickets", StringComparison.Ordinal));

        var composed = AdminNavComposition.Compose([new Nav(new AdminNavGroup("Tickets", [Item("Contributed")]))]);

        composed.Should().HaveCount(AdminNavTree.Groups.Count);
        var merged = composed.First(g => string.Equals(g.GroupKey, "Tickets", StringComparison.Ordinal));
        merged.Items.Should().HaveCount(existing.Items.Count + 1);
        merged.Items[^1].Label.Should().Be("Contributed");
    }

    /// <summary>
    /// The sidebar renders System groups as collapsed plumbing at the bottom, so a
    /// user-facing contribution has to land above them rather than below the divider.
    /// </summary>
    [HumansFact]
    public void Unknown_Group_Lands_Above_The_System_Zone_Ordered_By_Weight()
    {
        var composed = AdminNavComposition.Compose(
        [
            new Nav(new AdminNavGroup("Later", [Item("b")], Weight: 20)),
            new Nav(new AdminNavGroup("Sooner", [Item("a")], Weight: 10))
        ]);

        var firstSystem = composed.Select((g, i) => (g.System, i)).First(x => x.System).i;
        composed.Take(firstSystem).Select(g => g.Label).TakeLast(2).Should().Equal("Sooner", "Later");
    }

    [HumansFact]
    public void Negative_Weight_Lands_A_Group_Above_The_Tree()
    {
        var composed = AdminNavComposition.Compose(
            [new Nav(new AdminNavGroup("Urgent", [Item("a")], Weight: -5))]);

        composed[0].Label.Should().Be("Urgent");
    }

    /// <summary>
    /// The tree's groups all carry weight 0, so both a positive weight and no weight at all
    /// land last among the user-facing groups — the placement every lane relies on today.
    /// </summary>
    [HumansFact]
    public void Weight_At_Or_Above_Zero_Lands_A_Group_Last_Above_The_System_Zone()
    {
        var lastTreeGroup = AdminNavTree.Groups.Last(g => !g.System).Label;

        foreach (var weight in (int[])[0, 5])
        {
            var composed = AdminNavComposition.Compose(
                [new Nav(new AdminNavGroup("Late", [Item("a")], Weight: weight))]);

            var firstSystem = composed.Select((g, i) => (g.System, i)).First(x => x.System).i;
            composed[firstSystem - 1].Label.Should().Be("Late");
            composed[firstSystem - 2].Label.Should().Be(lastTreeGroup);
        }
    }

    [HumansFact]
    public void Contributed_System_Group_Appends_Below_The_System_Zone()
    {
        var composed = AdminNavComposition.Compose(
            [new Nav(new AdminNavGroup("Plumbing", [Item("a")], System: true))]);

        composed[^1].Label.Should().Be("Plumbing");
    }

    /// <summary>
    /// Tree items carry no weight, so a contribution lands after them unless it asks for a
    /// negative weight. Equal weights keep declared order — the sort is stable, which is what
    /// lets the tree's traffic-based order survive being merged into.
    /// </summary>
    [HumansFact]
    public void Weight_Places_A_Contribution_Around_The_Existing_Items()
    {
        var existing = AdminNavTree.Groups
            .First(g => string.Equals(g.GroupKey, "Cantina", StringComparison.Ordinal)).Items[0].Label;

        var composed = AdminNavComposition.Compose(
            [new Nav(new AdminNavGroup("Cantina", [Item("second"), Item("first", weight: -1)]))]);

        composed.First(g => string.Equals(g.GroupKey, "Cantina", StringComparison.Ordinal))
            .Items.Select(i => i.Label).Should().Equal("first", existing, "second");
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

    /// <summary>
    /// The <c>ISystemTeamSync</c> case: scheduled against an interface whose ExecuteAsync
    /// returns a report, so the built lambda must still type as <c>Func&lt;T, Task&gt;</c>.
    /// </summary>
    [HumansFact]
    public void Job_Call_Is_Built_For_An_Interface_Returning_A_Report()
    {
        var execute = typeof(IReportingJob).GetMethod(nameof(IReportingJob.ExecuteAsync), [typeof(CancellationToken)])!;

        Expression<Func<IReportingJob, Task>> call = RecurringJobExtensions.BuildCall<IReportingJob>(execute);

        call.ReturnType.Should().Be(typeof(Task));
    }

    private sealed class NotAJob;

    /// <summary>
    /// The roll-call is built before the per-job try/catch runs, so a section naming a type
    /// with no ExecuteAsync must not throw while the list is assembled — that would stop the
    /// app booting for every other job too.
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
    /// Top-level items sort by weight alone. Equal weights keep declared order rather than
    /// alphabetizing on the label — the same stable-order contract as
    /// <see cref="AdminNavComposition"/>, which is why neither carries a tie-break.
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

    /// <summary>
    /// Children carry the same Weight field as top-level items, so they order by it too. The
    /// sort is stable, which is what lets equal weights keep declared order.
    /// </summary>
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
    /// Contributions are <c>internal sealed</c> with no declared constructor, and
    /// <see cref="SectionDiscoveryExtensions.DiscoverImplementations{T}"/> activates them with
    /// the plain <c>Activator.CreateInstance(Type)</c> overload, which only finds public
    /// constructors. That works because C# emits the implicit constructor as public regardless
    /// of the class's accessibility — pinned here because declaring a non-public or
    /// parameterised constructor instead would break section discovery at startup.
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

/// <summary>
/// The documented contribution shape, top-level so its accessibility is the real thing rather
/// than a nested type's. Exists only for
/// <see cref="SectionSeamTests.Internal_Contribution_With_An_Implicit_Constructor_Activates"/>.
/// </summary>
internal sealed class InternalNavContribution : ISectionNav
{
    public IEnumerable<MemberNavItem> Items() => [new MemberNavItem("Contributed")];
}
