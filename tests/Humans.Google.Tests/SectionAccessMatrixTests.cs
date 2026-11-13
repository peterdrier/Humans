using AwesomeAssertions;
using Humans.Base.Interfaces;

namespace Humans.Google.Tests;

public class SectionAccessMatrixTests
{
    [HumansFact]
    public void Pins_the_GoogleSync_matrix()
    {
        var matrix = ((ISectionAccessMatrix)new Section()).AccessMatrices
            .Single(m => string.Equals(m.Key, "GoogleSync", StringComparison.Ordinal));

        (matrix.SectionName, matrix.Order).Should().Be(("Google Sync", 93));
        matrix.Roles.Should().Equal("Admin");
        matrix.Features
            .Select(f => $"{f.Name}: {string.Join(", ", f.RoleAccess.Select(kv => $"{kv.Key}={kv.Value}"))}")
            .Should().Equal(
                "Sync settings: Admin=Allowed");
    }
}
