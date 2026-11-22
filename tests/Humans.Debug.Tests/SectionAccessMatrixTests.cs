using AwesomeAssertions;
using Humans.Base.Interfaces;

namespace Humans.Debug.Tests;

public class SectionAccessMatrixTests
{
    [HumansFact]
    public void Pins_the_Debug_matrix()
    {
        var matrix = ((ISectionAccessMatrix)new Section()).AccessMatrices
            .Single(m => string.Equals(m.Key, "Debug", StringComparison.Ordinal));

        (matrix.SectionName, matrix.Order).Should().Be(("Debug", 95));
        matrix.Roles.Should().Equal("Board", "Admin");
        matrix.Features
            .Select(f => $"{f.Name}: {string.Join(", ", f.RoleAccess.Select(kv => $"{kv.Key}={kv.Value}"))}")
            .Should().Equal(
                "Configuration status: Board=Allowed, Admin=Allowed",
                "Background jobs: Board=Limited, Admin=Allowed");
    }
}
