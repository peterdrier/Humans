using Humans.Base.Interfaces;
using AwesomeAssertions;
using Humans.Base.Models;

namespace Humans.Governance.Tests;

public class SectionAccessMatrixTests
{
    private static AccessMatrixData Matrix(string key) =>
        ((ISectionAccessMatrix)new Section()).AccessMatrices.Single(m => string.Equals(m.Key, key, StringComparison.Ordinal));

    [HumansFact]
    public void Contributes_only_its_own_page_matrix()
    {
        ((ISectionAccessMatrix)new Section()).AccessMatrices
            .Select(m => (m.Key, m.SectionName, m.Order))
            .Should().Equal(("Governance", "Governance", 40));
    }

    [HumansFact]
    public void Pins_the_governance_feature_rows()
    {
        var matrix = Matrix("Governance");
        matrix.Roles.Should().Equal("Volunteer", "Board");
        matrix.Features
            .Select(f => $"{f.Name}: {string.Join(", ", f.RoleAccess.Select(kv => $"{kv.Key}={kv.Value}"))}")
            .Should().Equal(
                "View estatutos: Volunteer=Allowed, Board=Allowed",
                "Apply for tier: Volunteer=Allowed, Board=Allowed",
                "View applications: Volunteer=Limited, Board=Allowed",
                "Vote on applications: Volunteer=Denied, Board=Allowed");
    }
}
