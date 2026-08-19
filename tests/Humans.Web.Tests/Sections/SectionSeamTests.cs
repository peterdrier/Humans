using System.Linq.Expressions;
using AwesomeAssertions;
using Humans.Base.Interfaces;
using Humans.Web.Extensions;
using Humans.Web.ViewComponents;

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

    [HumansFact]
    public void Unknown_Group_Is_Appended_And_Groups_Order_By_Weight()
    {
        var composed = AdminNavComposition.Compose(
        [
            new Nav(new AdminNavGroup("Later", [Item("b")], Weight: 20)),
            new Nav(new AdminNavGroup("Sooner", [Item("a")], Weight: 10))
        ]);

        composed.Select(g => g.Label).TakeLast(2).Should().Equal("Sooner", "Later");
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
}
