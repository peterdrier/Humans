using Humans.Base.Interfaces;
using Humans.Base.Models;
using static Humans.Base.Models.AccessLevel;
using static Humans.Base.Models.AccessMatrixFeature;

namespace Humans.Governance;

/// <summary>
/// Help-widget access matrices for <c>/Governance</c>, the Board dashboard and the <c>/Admin</c>
/// tool frame — all three were rows in Base's deleted table. <c>/Admin</c> is a Shell nav holder
/// rather than a section of its own, and Shell may not declare a section's contribution, so the
/// Board's own section carries it: the same ownership <c>AgentSectionKeys</c> already records
/// ("Admin" and "Board" both resolve to Governance).
/// </summary>
internal sealed class SectionAccessMatrix : ISectionAccessMatrix
{
    public IReadOnlyList<AccessMatrixData> AccessMatrices =>
    [
        new()
        {
            Key = "Governance",
            SectionName = "Governance",
            Order = 40,
            Roles = ["Volunteer", "Board"],
            Features =
            [
                Of("View estatutos", ("Volunteer", Allowed), ("Board", Allowed)),
                Of("Apply for tier", ("Volunteer", Allowed), ("Board", Allowed)),
                Of("View applications", ("Volunteer", Limited), ("Board", Allowed)),
                Of("Vote on applications", ("Volunteer", Denied), ("Board", Allowed)),
            ]
        },
        new()
        {
            Key = "Board",
            SectionName = "Board Dashboard",
            Order = 60,
            Roles = ["Board"],
            Features =
            [
                Of("Dashboard & stats", ("Board", Allowed)),
                Of("Audit log", ("Board", Allowed)),
            ]
        },
        new()
        {
            Key = "Admin",
            SectionName = "Admin Tools",
            Order = 90,
            Roles = ["Admin"],
            Features =
            [
                Of("Configuration status", ("Admin", Allowed)),
                Of("Sync settings", ("Admin", Allowed)),
                Of("Email outbox", ("Admin", Allowed)),
                Of("Background jobs", ("Admin", Allowed)),
                Of("All humans list", ("Admin", Allowed)),
                Of("Role assignments", ("Admin", Allowed)),
                Of("Legal documents", ("Admin", Allowed)),
            ]
        }
    ];
}
