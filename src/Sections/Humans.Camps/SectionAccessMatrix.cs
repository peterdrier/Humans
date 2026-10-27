using Humans.Base.Models;
using static Humans.Base.Models.AccessLevel;
using static Humans.Base.Models.AccessMatrixFeature;

namespace Humans.Camps;

internal sealed class SectionAccessMatrix : ISectionAccessMatrix
{
    public IReadOnlyList<AccessMatrixData> AccessMatrices =>
    [
        new()
        {
            Key = "Camps",
            SectionName = "Barrios",
            Order = 30,
            Roles = ["Volunteer", "Camp Lead", "CampsAdmin"],
            Features =
            [
                Of("Browse camps", ("Volunteer", Allowed), ("Camp Lead", Allowed), ("CampsAdmin", Allowed)),
                Of("Register a camp", ("Volunteer", Allowed), ("Camp Lead", Allowed), ("CampsAdmin", Allowed)),
                Of("Edit own camp", ("Volunteer", Denied), ("Camp Lead", Allowed), ("CampsAdmin", Allowed)),
                Of("Approve/reject camps", ("Volunteer", Denied), ("Camp Lead", Denied), ("CampsAdmin", Allowed)),
                Of("Camp settings", ("Volunteer", Denied), ("Camp Lead", Denied), ("CampsAdmin", Allowed)),
            ]
        }
    ];
}
