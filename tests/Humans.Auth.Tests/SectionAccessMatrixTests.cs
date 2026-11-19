using AwesomeAssertions;
using Humans.Base.Interfaces;

namespace Humans.Auth.Tests;

public class SectionAccessMatrixTests
{
    [HumansFact]
    public void Pins_the_RoleAssignments_matrix()
    {
        var matrix = ((ISectionAccessMatrix)new Section()).AccessMatrices
            .Single(m => string.Equals(m.Key, "RoleAssignments", StringComparison.Ordinal));

        (matrix.SectionName, matrix.Order).Should().Be(("Role Assignments", 91));
        matrix.Roles.Should().Equal("Board", "UsersAdmin", "Admin");
        matrix.Features
            .Select(f => $"{f.Name}: {string.Join(", ", f.RoleAccess.Select(kv => $"{kv.Key}={kv.Value}"))}")
            .Should().Equal(
                "View role assignments: Board=Allowed, UsersAdmin=Allowed, Admin=Allowed",
                "Add or end role assignments: Board=Denied, UsersAdmin=Allowed, Admin=Allowed");
    }
}
