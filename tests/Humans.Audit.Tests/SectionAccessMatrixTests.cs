using AwesomeAssertions;
using Humans.Base.Interfaces;

namespace Humans.Audit.Tests;

public class SectionAccessMatrixTests
{
    [HumansFact]
    public void AccessMatrix_Audit_MatchesPinnedRows()
    {
        var matrix = ((ISectionAccessMatrix)new Section()).AccessMatrices
            .Single(m => string.Equals(m.Key, "Audit", StringComparison.Ordinal));

        (matrix.SectionName, matrix.Order).Should().Be(("Audit", 60));
        matrix.Roles.Should().Equal("Board", "Admin");
        matrix.Features
            .Select(f => $"{f.Name}: {string.Join(", ", f.RoleAccess.Select(kv => $"{kv.Key}={kv.Value}"))}")
            .Should().Equal(
                "View the audit log: Board=Allowed, Admin=Allowed");
    }
}
