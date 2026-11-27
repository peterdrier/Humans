using AwesomeAssertions;
using Humans.Base.Interfaces;

namespace Humans.Auth.Tests;

public class SectionAccessMatrixTests
{
    [HumansFact]
    public void AccessMatrix_MatchesRoleAssignmentsPin()
    {
        var matrix = ((ISectionAccessMatrix)new Section()).AccessMatrices
            .Single(m => string.Equals(m.Key, "SystemRoles", StringComparison.Ordinal));

        (matrix.SectionName, matrix.Order).Should().Be(("System Roles", 91));
        matrix.Roles.Should().Equal("Board", "UsersAdmin", "Admin");
        matrix.Features
            .Select(f => $"{f.Name}: {string.Join(", ", f.RoleAccess.Select(kv => $"{kv.Key}={kv.Value}"))}")
            .Should().Equal(
                "View system roles: Board=Allowed, UsersAdmin=Allowed, Admin=Allowed",
                "Assign or revoke system roles: Board=Denied, UsersAdmin=Allowed, Admin=Allowed");
    }
}
