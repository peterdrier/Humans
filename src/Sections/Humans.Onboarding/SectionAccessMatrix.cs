using Humans.Base.Interfaces;
using Humans.Base.Models;
using static Humans.Base.Models.AccessLevel;
using static Humans.Base.Models.AccessMatrixFeature;

namespace Humans.Onboarding;

/// <summary>Help-widget access matrix for the onboarding review queue — was a row in Base's deleted table.</summary>
internal sealed class SectionAccessMatrix : ISectionAccessMatrix
{
    public IReadOnlyList<AccessMatrixData> AccessMatrices =>
    [
        new()
        {
            Key = "OnboardingReview",
            SectionName = "Onboarding Review",
            Order = 50,
            Roles = ["ConsentCoordinator", "VolunteerCoordinator", "Board"],
            Features =
            [
                Of("View onboarding queue", ("ConsentCoordinator", Allowed), ("VolunteerCoordinator", Allowed), ("Board", Allowed)),
                Of("Clear consent checks", ("ConsentCoordinator", Allowed), ("VolunteerCoordinator", Denied), ("Board", Allowed)),
                Of("Flag / reject signup", ("ConsentCoordinator", Allowed), ("VolunteerCoordinator", Denied), ("Board", Allowed)),
                Of("Board voting", ("ConsentCoordinator", Denied), ("VolunteerCoordinator", Denied), ("Board", Allowed)),
            ]
        }
    ];
}
