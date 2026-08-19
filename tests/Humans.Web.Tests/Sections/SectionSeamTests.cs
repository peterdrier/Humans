using System.Linq.Expressions;
using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base.Interfaces;
using Humans.Web.Extensions;
using Humans.Web.Hosting;
using Microsoft.Extensions.Configuration;
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
    public void Contributing_Nothing_Composes_Nothing()
    {
        AdminNavComposition.Compose([]).Should().BeEmpty();
    }

    [HumansFact]
    public void Contribution_Merges_Into_An_Existing_Group_By_Key()
    {
        var composed = AdminNavComposition.Compose(
        [
            new Nav(new AdminNavGroup("Tickets", [Item("Existing")])),
            new Nav(new AdminNavGroup("Tickets", [Item("Contributed")]))
        ]);

        composed.Should().ContainSingle(g => string.Equals(g.GroupKey, "Tickets", StringComparison.Ordinal));
        var merged = composed.First(g => string.Equals(g.GroupKey, "Tickets", StringComparison.Ordinal));
        merged.Items.Select(i => i.Label).Should().Equal("Existing", "Contributed");
    }

    [HumansFact]
    public void Unknown_Group_Is_Appended_And_Groups_Order_By_Weight()
    {
        var composed = AdminNavComposition.Compose(
        [
            new Nav(new AdminNavGroup("Later", [Item("b")], Weight: 20)),
            new Nav(new AdminNavGroup("Sooner", [Item("a")], Weight: 10))
        ]);

        composed.Select(g => g.Label).Should().Equal("Sooner", "Later");
    }

    /// <summary>
    /// The sidebar renders System groups as collapsed plumbing at the bottom, so a System
    /// contribution lands below every user-facing group however heavy those are.
    /// </summary>
    [HumansFact]
    public void Contributed_System_Group_Lands_Below_The_User_Facing_Groups()
    {
        var composed = AdminNavComposition.Compose(
        [
            new Nav(new AdminNavGroup("Plumbing", [Item("a")], System: true)),
            new Nav(new AdminNavGroup("Heavy", [Item("b")], Weight: 99))
        ]);

        composed.Select(g => g.Label).Should().Equal("Heavy", "Plumbing");
    }

    /// <summary>
    /// Equal weights keep declared order — the sort is stable, which is what lets a group's
    /// traffic-based item order survive being merged into by another section's contribution.
    /// </summary>
    [HumansFact]
    public void Weight_Places_A_Contribution_Around_The_Existing_Items()
    {
        var composed = AdminNavComposition.Compose(
        [
            new Nav(new AdminNavGroup("Cantina", [Item("existing")])),
            new Nav(new AdminNavGroup("Cantina", [Item("second"), Item("first", weight: -1)]))
        ]);

        composed.First(g => string.Equals(g.GroupKey, "Cantina", StringComparison.Ordinal))
            .Items.Select(i => i.Label).Should().Equal("first", "existing", "second");
    }

    /// <summary>
    /// Discovery activates the real section contributions, which are <c>internal sealed</c>:
    /// a class's compiler-generated default constructor is public even when the class is not,
    /// so <c>Activator.CreateInstance(Type)</c> reaches them without non-public binding flags.
    /// </summary>
    [HumansFact]
    public void Internal_Section_Contributions_Are_Reflection_Constructible()
    {
        var navs = SectionDiscoveryExtensions.DiscoverImplementations<ISectionAdminNav>(
            SectionAssemblySnapshot.For(new ConfigurationBuilder().Build()));

        navs.Should().NotBeEmpty();
        navs.Should().OnlyContain(n => !n.GetType().IsPublic, "contributions stay off the section's public surface");
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
