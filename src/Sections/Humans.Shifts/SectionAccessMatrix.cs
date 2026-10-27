using Humans.Base.Interfaces;
using Humans.Base.Models;
using static Humans.Base.Models.AccessLevel;
using static Humans.Base.Models.AccessMatrixFeature;

namespace Humans.Shifts;

internal sealed class SectionAccessMatrix : ISectionAccessMatrix
{
    public IReadOnlyList<AccessMatrixData> AccessMatrices =>
    [
        new()
        {
            Key = "Shifts",
            SectionName = "Shifts",
            Order = 10,
            Roles = ["Volunteer", "Coordinator", "ShiftsNoInfo", "ShiftsAdmin"],
            Features =
            [
                Of("Browse shifts", ("Volunteer", Allowed), ("Coordinator", Allowed), ("ShiftsNoInfo", Allowed), ("ShiftsAdmin", Allowed)),
                Of("Sign up for shifts", ("Volunteer", Allowed), ("Coordinator", Allowed), ("ShiftsNoInfo", Allowed), ("ShiftsAdmin", Allowed)),
                Of("My Shifts & availability", ("Volunteer", Allowed), ("Coordinator", Allowed), ("ShiftsNoInfo", Allowed), ("ShiftsAdmin", Allowed)),
                Of("Create/edit rotas & shifts", ("Volunteer", Denied), ("Coordinator", Allowed), ("ShiftsNoInfo", Denied), ("ShiftsAdmin", Allowed)),
                Of("Approve/refuse signups", ("Volunteer", Denied), ("Coordinator", Allowed), ("ShiftsNoInfo", Allowed), ("ShiftsAdmin", Allowed)),
                Of("Voluntell", ("Volunteer", Denied), ("Coordinator", Allowed), ("ShiftsNoInfo", Allowed), ("ShiftsAdmin", Allowed)),
                Of("Staffing dashboard", ("Volunteer", Denied), ("Coordinator", Denied), ("ShiftsNoInfo", Allowed), ("ShiftsAdmin", Allowed)),
            ]
        }
    ];
}
