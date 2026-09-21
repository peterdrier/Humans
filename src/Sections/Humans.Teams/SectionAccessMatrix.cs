using Humans.Base.Interfaces;
using Humans.Base.Models;
using static Humans.Base.Models.AccessLevel;
using static Humans.Base.Models.AccessMatrixFeature;

namespace Humans.Teams;

/// <summary>Help-widget access matrix for <c>/Team</c> — was a row in Base's deleted table.</summary>
internal sealed class SectionAccessMatrix : ISectionAccessMatrix
{
    public IReadOnlyList<AccessMatrixData> AccessMatrices =>
    [
        new()
        {
            Key = "Teams",
            SectionName = "Teams",
            Order = 20,
            Roles = ["Volunteer", "Coordinator", "Board", "TeamsAdmin"],
            Features =
            [
                Of("View teams & join", ("Volunteer", Allowed), ("Coordinator", Allowed), ("Board", Allowed), ("TeamsAdmin", Allowed)),
                Of("View team details", ("Volunteer", Allowed), ("Coordinator", Allowed), ("Board", Allowed), ("TeamsAdmin", Allowed)),
                Of("Manage members", ("Volunteer", Denied), ("Coordinator", Allowed), ("Board", Allowed), ("TeamsAdmin", Allowed)),
                Of("Manage roles", ("Volunteer", Denied), ("Coordinator", Allowed), ("Board", Allowed), ("TeamsAdmin", Allowed)),
                Of("Create teams", ("Volunteer", Denied), ("Coordinator", Denied), ("Board", Allowed), ("TeamsAdmin", Allowed)),
                Of("Delete teams", ("Volunteer", Denied), ("Coordinator", Denied), ("Board", Allowed), ("TeamsAdmin", Denied)),
                Of("Google resource sync", ("Volunteer", Denied), ("Coordinator", Denied), ("Board", Denied), ("TeamsAdmin", Limited)),
            ]
        }
    ];
}
