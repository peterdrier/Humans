using AwesomeAssertions;
using Humans.Base.Models;

namespace Humans.Onboarding.Tests;

public class SectionAccessMatrixTests
{
    private static AccessMatrixData Matrix() =>
        new SectionAccessMatrix().AccessMatrices.Should().ContainSingle().Subject;

    [HumansFact]
    public void Contributes_the_OnboardingReview_matrix_under_the_key_the_page_calls_it_with()
    {
        var matrix = Matrix();

        matrix.Key.Should().Be("OnboardingReview");
        matrix.SectionName.Should().Be("Onboarding Review");
        matrix.Order.Should().Be(50);
        matrix.Roles.Should().Equal("ConsentCoordinator", "ShiftsAdmin", "Board");
    }

    /// <summary>
    /// The Consent Coordinator clears and flags; the Shifts Admin does neither. That
    /// negative is the point of the table, so it is pinned literally.
    /// </summary>
    [HumansFact]
    public void Pins_every_feature_row()
    {
        Matrix().Features
            .Select(f => $"{f.Name}: {string.Join(", ", f.RoleAccess.Select(kv => $"{kv.Key}={kv.Value}"))}")
            .Should().Equal(
                "View onboarding queue: ConsentCoordinator=Allowed, ShiftsAdmin=Allowed, Board=Allowed",
                "Clear consent checks: ConsentCoordinator=Allowed, ShiftsAdmin=Denied, Board=Allowed",
                "Flag / reject signup: ConsentCoordinator=Allowed, ShiftsAdmin=Denied, Board=Allowed",
                "Board voting: ConsentCoordinator=Denied, ShiftsAdmin=Denied, Board=Allowed");
    }
}
