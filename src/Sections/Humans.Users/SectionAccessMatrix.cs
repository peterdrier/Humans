using Humans.Base.Interfaces;
using Humans.Base.Models;
using static Humans.Base.Models.AccessLevel;
using static Humans.Base.Models.AccessMatrixFeature;

namespace Humans.Users;

/// <summary>Help-widget access matrix for <c>/Profile</c>.</summary>
internal sealed class SectionAccessMatrix : ISectionAccessMatrix
{
    public IReadOnlyList<AccessMatrixData> AccessMatrices =>
    [
        new()
        {
            Key = "Profile",
            SectionName = "Profile",
            Order = 80,
            Roles = ["Volunteer", "Coordinator", "Board", "UsersAdmin", "Admin"],
            Features =
            [
                Of("View own profile", ("Volunteer", Allowed), ("Coordinator", Allowed), ("Board", Allowed), ("UsersAdmin", Allowed), ("Admin", Allowed)),
                Of("Edit own profile", ("Volunteer", Allowed), ("Coordinator", Allowed), ("Board", Allowed), ("UsersAdmin", Allowed), ("Admin", Allowed)),
                Of("View other profiles", ("Volunteer", Limited), ("Coordinator", Allowed), ("Board", Allowed), ("UsersAdmin", Allowed), ("Admin", Allowed)),
                Of("View contact fields", ("Volunteer", Limited), ("Coordinator", Limited), ("Board", Allowed), ("UsersAdmin", Allowed), ("Admin", Allowed)),
                Of("Admin view of profile", ("Volunteer", Denied), ("Coordinator", Denied), ("Board", Allowed), ("UsersAdmin", Allowed), ("Admin", Allowed)),
            ]
        }
    ];
}
