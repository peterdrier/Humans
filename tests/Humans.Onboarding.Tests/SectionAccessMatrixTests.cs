using AwesomeAssertions;
using Humans.Base.Models;

namespace Humans.Onboarding.Tests;

/// <summary>
/// The help widget and the agent's preload both read this section's access matrix through the
/// <c>ISectionAccessMatrix</c> seam, so the rows are pinned here rather than in Base.
/// </summary>
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
        matrix.Roles.Should().Equal("ConsentCoordinator", "VolunteerCoordinator", "Board");
    }

    /// <summary>
    /// The Consent Coordinator clears and flags; the Volunteer Coordinator does neither. That
    /// negative is the point of the table, so it is pinned literally.
    /// </summary>
    [HumansFact]
    public void Pins_every_feature_row()
    {
        Matrix().Features
            .Select(f => $"{f.Name}: {string.Join(", ", f.RoleAccess.Select(kv => $"{kv.Key}={kv.Value}"))}")
            .Should().Equal(
                "View onboarding queue: ConsentCoordinator=Allowed, VolunteerCoordinator=Allowed, Board=Allowed",
                "Clear consent checks: ConsentCoordinator=Allowed, VolunteerCoordinator=Denied, Board=Allowed",
                "Flag / reject signup: ConsentCoordinator=Allowed, VolunteerCoordinator=Denied, Board=Allowed",
                "Board voting: ConsentCoordinator=Denied, VolunteerCoordinator=Denied, Board=Allowed");
    }
}
