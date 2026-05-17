using AwesomeAssertions;
using Humans.Application.Interfaces.Shifts;
using Humans.Web.Models.Shifts;

namespace Humans.Web.Tests.Shifts;

/// <summary>
/// Display ordering for the coverage-pie row lives in the view-model
/// assembly (memory/architecture/display-sort-in-controllers). The service
/// returns natural <c>TeamName</c> order; the page builder applies the
/// "promoted sub-team next to parent" rule before stuffing the view model.
/// </summary>
public class ShiftBrowsePageBuilderPieSortTests
{
    [HumansFact]
    public void PromotedSubteam_RendersImmediatelyAfterItsParent_NotByOwnName()
    {
        var mangoId = Guid.NewGuid();
        var pies = new List<DepartmentCoveragePie>
        {
            new(Guid.NewGuid(), "Apple Slice", "apple-slice",
                IsSubTeam: true, ParentTeamId: mangoId, ParentTeamName: "Mango",
                RequestedHours: 4m, FilledHours: 0m),
            new(Guid.NewGuid(), "Banana", "banana",
                IsSubTeam: false, ParentTeamId: null, ParentTeamName: null,
                RequestedHours: 4m, FilledHours: 0m),
            new(mangoId, "Mango", "mango",
                IsSubTeam: false, ParentTeamId: null, ParentTeamName: null,
                RequestedHours: 4m, FilledHours: 0m),
        };

        var ordered = ShiftBrowsePageBuilder.OrderPiesGroupedByParent(pies);

        // Pure alphabetical would give: Apple Slice, Banana, Mango.
        // Grouped-by-parent: each promoted sub-team follows its parent.
        ordered.Select(p => p.TeamName).Should().Equal("Banana", "Mango", "Apple Slice");
    }

    [HumansFact]
    public void TopLevelOnly_FallsBackToAlphabetical()
    {
        var pies = new List<DepartmentCoveragePie>
        {
            new(Guid.NewGuid(), "Charlie", "charlie",
                IsSubTeam: false, ParentTeamId: null, ParentTeamName: null,
                RequestedHours: 1m, FilledHours: 0m),
            new(Guid.NewGuid(), "Alpha", "alpha",
                IsSubTeam: false, ParentTeamId: null, ParentTeamName: null,
                RequestedHours: 1m, FilledHours: 0m),
            new(Guid.NewGuid(), "Bravo", "bravo",
                IsSubTeam: false, ParentTeamId: null, ParentTeamName: null,
                RequestedHours: 1m, FilledHours: 0m),
        };

        var ordered = ShiftBrowsePageBuilder.OrderPiesGroupedByParent(pies);

        ordered.Select(p => p.TeamName).Should().Equal("Alpha", "Bravo", "Charlie");
    }

    [HumansFact]
    public void MultipleSubteamsUnderSameParent_SortAlphabeticallyAfterParent()
    {
        var parentId = Guid.NewGuid();
        var pies = new List<DepartmentCoveragePie>
        {
            new(Guid.NewGuid(), "Zeta", "zeta",
                IsSubTeam: true, ParentTeamId: parentId, ParentTeamName: "Mango",
                RequestedHours: 1m, FilledHours: 0m),
            new(parentId, "Mango", "mango",
                IsSubTeam: false, ParentTeamId: null, ParentTeamName: null,
                RequestedHours: 1m, FilledHours: 0m),
            new(Guid.NewGuid(), "Alpha", "alpha",
                IsSubTeam: true, ParentTeamId: parentId, ParentTeamName: "Mango",
                RequestedHours: 1m, FilledHours: 0m),
        };

        var ordered = ShiftBrowsePageBuilder.OrderPiesGroupedByParent(pies);

        ordered.Select(p => p.TeamName).Should().Equal("Mango", "Alpha", "Zeta");
    }
}
