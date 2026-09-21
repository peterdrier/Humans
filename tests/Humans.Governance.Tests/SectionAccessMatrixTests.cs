using AwesomeAssertions;
using Humans.Base.Models;

namespace Humans.Governance.Tests;

/// <summary>
/// Governance contributes three matrices: its own page, the Board dashboard, and the
/// <c>/Admin</c> tool frame — <c>/Admin</c> is a Shell nav holder rather than a section, and
/// Shell may not declare a contribution, so the Board's section carries it.
/// </summary>
public class SectionAccessMatrixTests
{
    private static AccessMatrixData Matrix(string key) =>
        new SectionAccessMatrix().AccessMatrices.Single(m => string.Equals(m.Key, key, StringComparison.Ordinal));

    [HumansFact]
    public void Contributes_the_governance_board_and_admin_matrices_in_that_order()
    {
        new SectionAccessMatrix().AccessMatrices
            .Select(m => (m.Key, m.SectionName, m.Order))
            .Should().Equal(
                ("Governance", "Governance", 40),
                ("Board", "Board Dashboard", 60),
                ("Admin", "Admin Tools", 90));
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

    [HumansFact]
    public void Pins_the_board_dashboard_feature_rows()
    {
        var matrix = Matrix("Board");
        matrix.Roles.Should().Equal("Board");
        matrix.Features
            .Select(f => $"{f.Name}: {string.Join(", ", f.RoleAccess.Select(kv => $"{kv.Key}={kv.Value}"))}")
            .Should().Equal(
                "Dashboard & stats: Board=Allowed",
                "Audit log: Board=Allowed");
    }

    [HumansFact]
    public void Pins_the_admin_tools_feature_rows()
    {
        var matrix = Matrix("Admin");
        matrix.Roles.Should().Equal("Admin");
        matrix.Features
            .Select(f => $"{f.Name}: {string.Join(", ", f.RoleAccess.Select(kv => $"{kv.Key}={kv.Value}"))}")
            .Should().Equal(
                "Configuration status: Admin=Allowed",
                "Sync settings: Admin=Allowed",
                "Email outbox: Admin=Allowed",
                "Background jobs: Admin=Allowed",
                "All humans list: Admin=Allowed",
                "Role assignments: Admin=Allowed",
                "Legal documents: Admin=Allowed");
    }
}
