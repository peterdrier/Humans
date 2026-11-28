using Humans.Base.Interfaces;
using AwesomeAssertions;
using Humans.Base.Models;

namespace Humans.Tickets.Tests;

public class SectionAccessMatrixTests
{
    private static AccessMatrixData Matrix() =>
        ((ISectionAccessMatrix)new Section()).AccessMatrices.Should().ContainSingle().Subject;

    [HumansFact]
    public void AccessMatrix_Tickets_UsesPageKey()
    {
        var matrix = Matrix();

        matrix.Key.Should().Be("Tickets");
        matrix.SectionName.Should().Be("Tickets");
        matrix.Order.Should().Be(70);
        matrix.Roles.Should().Equal("Board", "TicketsAdmin");
    }

    [HumansFact]
    public void AccessMatrix_MatchesFeatureRowPin()
    {
        Matrix().Features
            .Select(f => $"{f.Name}: {string.Join(", ", f.RoleAccess.Select(kv => $"{kv.Key}={kv.Value}"))}")
            .Should().Equal(
                "View tickets & orders: Board=Allowed, TicketsAdmin=Allowed",
                "Sync operations: Board=Denied, TicketsAdmin=Allowed",
                "Discount codes: Board=Allowed, TicketsAdmin=Allowed");
    }
}
