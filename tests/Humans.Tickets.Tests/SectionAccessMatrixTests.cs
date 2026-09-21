using AwesomeAssertions;
using Humans.Base.Models;

namespace Humans.Tickets.Tests;

/// <summary>
/// The help widget and the agent's preload both read this section's access matrix through the
/// <c>ISectionAccessMatrix</c> seam, so the rows are pinned here rather than in Base.
/// </summary>
public class SectionAccessMatrixTests
{
    private static AccessMatrixData Matrix() =>
        new SectionAccessMatrix().AccessMatrices.Should().ContainSingle().Subject;

    [HumansFact]
    public void Contributes_the_Tickets_matrix_under_the_key_the_page_calls_it_with()
    {
        var matrix = Matrix();

        matrix.Key.Should().Be("Tickets");
        matrix.SectionName.Should().Be("Tickets");
        matrix.Order.Should().Be(70);
        matrix.Roles.Should().Equal("Board", "TicketAdmin");
    }

    [HumansFact]
    public void Pins_every_feature_row()
    {
        Matrix().Features
            .Select(f => $"{f.Name}: {string.Join(", ", f.RoleAccess.Select(kv => $"{kv.Key}={kv.Value}"))}")
            .Should().Equal(
                "View tickets & orders: Board=Allowed, TicketAdmin=Allowed",
                "Sync operations: Board=Denied, TicketAdmin=Allowed",
                "Discount codes: Board=Allowed, TicketAdmin=Allowed");
    }
}
