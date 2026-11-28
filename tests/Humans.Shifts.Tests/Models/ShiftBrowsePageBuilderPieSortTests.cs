using AwesomeAssertions;
using Humans.Shifts.Services;
using Humans.Shifts.Models;

namespace Humans.Shifts.Tests.Models;

/// <summary>
/// The page builder orders departments by name and places each promoted sub-team next to its parent.
/// </summary>
public class ShiftBrowsePageBuilderPieSortTests
{
    [HumansFact]
    public void Pies_PromotedSubteam_FollowsItsParent()
    {
        var mangoId = Guid.NewGuid();
        var pies = new List<DepartmentCoveragePie>
        {
            new(Guid.NewGuid(), "Apple Slice",
                IsSubTeam: true, ParentTeamId: mangoId, ParentTeamName: "Mango",
                RequestedHours: 4m, FilledHours: 0m),
            new(Guid.NewGuid(), "Banana",
                IsSubTeam: false, ParentTeamId: null, ParentTeamName: null,
                RequestedHours: 4m, FilledHours: 0m),
            new(mangoId, "Mango",
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
            new(Guid.NewGuid(), "Charlie",
                IsSubTeam: false, ParentTeamId: null, ParentTeamName: null,
                RequestedHours: 1m, FilledHours: 0m),
            new(Guid.NewGuid(), "Alpha",
                IsSubTeam: false, ParentTeamId: null, ParentTeamName: null,
                RequestedHours: 1m, FilledHours: 0m),
            new(Guid.NewGuid(), "Bravo",
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
            new(Guid.NewGuid(), "Zeta",
                IsSubTeam: true, ParentTeamId: parentId, ParentTeamName: "Mango",
                RequestedHours: 1m, FilledHours: 0m),
            new(parentId, "Mango",
                IsSubTeam: false, ParentTeamId: null, ParentTeamName: null,
                RequestedHours: 1m, FilledHours: 0m),
            new(Guid.NewGuid(), "Alpha",
                IsSubTeam: true, ParentTeamId: parentId, ParentTeamName: "Mango",
                RequestedHours: 1m, FilledHours: 0m),
        };

        var ordered = ShiftBrowsePageBuilder.OrderPiesGroupedByParent(pies);

        ordered.Select(p => p.TeamName).Should().Equal("Mango", "Alpha", "Zeta");
    }
}
