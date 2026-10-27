using Humans.Base.Interfaces;
using Humans.Base.Models;
using static Humans.Base.Models.AccessLevel;
using static Humans.Base.Models.AccessMatrixFeature;

namespace Humans.Onboarding;

internal sealed class SectionAccessMatrix : ISectionAccessMatrix
{
    public IReadOnlyList<AccessMatrixData> AccessMatrices =>
    [
        new()
        {
            Key = "OnboardingReview",
            SectionName = "Onboarding Review",
            Order = 50,
            Roles = ["ConsentCoordinator", "ShiftsAdmin", "Board"],
            Features =
            [
                Of("View onboarding queue", ("ConsentCoordinator", Allowed), ("ShiftsAdmin", Allowed), ("Board", Allowed)),
                Of("Clear consent checks", ("ConsentCoordinator", Allowed), ("ShiftsAdmin", Denied), ("Board", Allowed)),
                Of("Flag / reject signup", ("ConsentCoordinator", Allowed), ("ShiftsAdmin", Denied), ("Board", Allowed)),
                Of("Board voting", ("ConsentCoordinator", Denied), ("ShiftsAdmin", Denied), ("Board", Allowed)),
            ]
        }
    ];
}
