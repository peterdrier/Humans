using Humans.Base.Interfaces;
using Humans.Base.Models;
using static Humans.Base.Models.AccessLevel;
using static Humans.Base.Models.AccessMatrixFeature;

namespace Humans.Camps;

/// <summary>Help-widget access matrix for <c>/Camp</c> — was a row in Base's deleted table.</summary>
internal sealed class SectionAccessMatrix : ISectionAccessMatrix
{
    public IReadOnlyList<AccessMatrixData> AccessMatrices =>
    [
        new()
        {
            Key = "Camps",
            SectionName = "Barrios",
            Order = 30,
            Roles = ["Volunteer", "Camp Lead", "CampAdmin"],
            Features =
            [
                Of("Browse camps", ("Volunteer", Allowed), ("Camp Lead", Allowed), ("CampAdmin", Allowed)),
                Of("Register a camp", ("Volunteer", Allowed), ("Camp Lead", Allowed), ("CampAdmin", Allowed)),
                Of("Edit own camp", ("Volunteer", Denied), ("Camp Lead", Allowed), ("CampAdmin", Allowed)),
                Of("Approve/reject camps", ("Volunteer", Denied), ("Camp Lead", Denied), ("CampAdmin", Allowed)),
                Of("Camp settings", ("Volunteer", Denied), ("Camp Lead", Denied), ("CampAdmin", Allowed)),
            ]
        }
    ];
}
