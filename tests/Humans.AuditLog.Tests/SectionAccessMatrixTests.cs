using AwesomeAssertions;
using Humans.Base.Interfaces;

namespace Humans.AuditLog.Tests;

public class SectionAccessMatrixTests
{
    [HumansFact]
    public void Pins_the_AuditLog_matrix()
    {
        var matrix = ((ISectionAccessMatrix)new Section()).AccessMatrices
            .Single(m => string.Equals(m.Key, "AuditLog", StringComparison.Ordinal));

        (matrix.SectionName, matrix.Order).Should().Be(("Audit Log", 60));
        matrix.Roles.Should().Equal("Board", "Admin");
        matrix.Features
            .Select(f => $"{f.Name}: {string.Join(", ", f.RoleAccess.Select(kv => $"{kv.Key}={kv.Value}"))}")
            .Should().Equal(
                "View the audit log: Board=Allowed, Admin=Allowed");
    }
}
