using AwesomeAssertions;
using Humans.Base.Interfaces;
using Humans.Base.ViewComponents;

namespace Humans.Base.Tests.ViewComponents;

public class AdminNavCompositionTests
{
    private static readonly IReadOnlyList<AdminNavGroup> Groups =
    [
        new("Debug", [
            new("Logs",     "Debug", "Logs",    null, null, "icon", null),
            new("DB stats", "Debug", "DbStats", null, null, "icon", null)
        ])
    ];

    [HumansFact]
    public void Locate_SharedController_MatchesByAction()
    {
        // Regression: DebugController has several items; a controller-only match returned the first.
        AdminNavComposition.Locate(Groups, "Debug", "DbStats", parent: null)!.Item.Label.Should().Be("DB stats");
    }

    [HumansFact]
    public void Locate_NamedParent_WinsOverFirstItem()
    {
        var location = AdminNavComposition.Locate(Groups, "Debug", "LogDetail", parent: "DbStats")!;

        location.Item.Label.Should().Be("DB stats");
        location.IsExact.Should().BeFalse();
    }
}
