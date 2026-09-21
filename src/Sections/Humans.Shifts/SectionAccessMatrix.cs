using Humans.Base.Interfaces;
using Humans.Base.Models;
using static Humans.Base.Models.AccessLevel;
using static Humans.Base.Models.AccessMatrixFeature;

namespace Humans.Shifts;

/// <summary>Help-widget access matrix for <c>/Shifts</c> — was a row in Base's deleted table.</summary>
internal sealed class SectionAccessMatrix : ISectionAccessMatrix
{
    public IReadOnlyList<AccessMatrixData> AccessMatrices =>
    [
        new()
        {
            Key = "Shifts",
            SectionName = "Shifts",
            Order = 10,
            Roles = ["Volunteer", "Coordinator", "NoInfoAdmin", "VolunteerCoordinator"],
            Features =
            [
                Of("Browse shifts", ("Volunteer", Allowed), ("Coordinator", Allowed), ("NoInfoAdmin", Allowed), ("VolunteerCoordinator", Allowed)),
                Of("Sign up for shifts", ("Volunteer", Allowed), ("Coordinator", Allowed), ("NoInfoAdmin", Allowed), ("VolunteerCoordinator", Allowed)),
                Of("My Shifts & availability", ("Volunteer", Allowed), ("Coordinator", Allowed), ("NoInfoAdmin", Allowed), ("VolunteerCoordinator", Allowed)),
                Of("Create/edit rotas & shifts", ("Volunteer", Denied), ("Coordinator", Allowed), ("NoInfoAdmin", Denied), ("VolunteerCoordinator", Allowed)),
                Of("Approve/refuse signups", ("Volunteer", Denied), ("Coordinator", Allowed), ("NoInfoAdmin", Allowed), ("VolunteerCoordinator", Allowed)),
                Of("Voluntell", ("Volunteer", Denied), ("Coordinator", Allowed), ("NoInfoAdmin", Allowed), ("VolunteerCoordinator", Allowed)),
                Of("Staffing dashboard", ("Volunteer", Denied), ("Coordinator", Denied), ("NoInfoAdmin", Allowed), ("VolunteerCoordinator", Allowed)),
            ]
        }
    ];
}
