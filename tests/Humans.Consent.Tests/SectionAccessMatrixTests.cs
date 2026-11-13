using Humans.Base.Interfaces;
using AwesomeAssertions;
using Humans.Base.Models;

namespace Humans.Consent.Tests;

public class SectionAccessMatrixTests
{
    private static AccessMatrixData Matrix() =>
        ((ISectionAccessMatrix)new Section()).AccessMatrices.Single(m => string.Equals(m.Key, "ConsentReview", StringComparison.Ordinal));

    [HumansFact]
    public void Contributes_the_ConsentReview_matrix_under_the_key_the_page_calls_it_with()
    {
        var matrix = Matrix();

        matrix.Key.Should().Be("ConsentReview");
        matrix.SectionName.Should().Be("Consent Review");
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
                "View review queue: ConsentCoordinator=Allowed, ShiftsAdmin=Allowed, Board=Allowed",
                "Clear consent checks: ConsentCoordinator=Allowed, ShiftsAdmin=Denied, Board=Allowed",
                "Flag / reject signup: ConsentCoordinator=Allowed, ShiftsAdmin=Denied, Board=Allowed",
                "Board voting: ConsentCoordinator=Denied, ShiftsAdmin=Denied, Board=Allowed");
    }

    [HumansFact]
    public void Pins_the_LegalDocuments_matrix()
    {
        var matrix = ((ISectionAccessMatrix)new Section()).AccessMatrices
            .Single(m => string.Equals(m.Key, "LegalDocuments", StringComparison.Ordinal));

        (matrix.SectionName, matrix.Order).Should().Be(("Legal Documents", 92));
        matrix.Roles.Should().Equal("Board", "Admin");
        matrix.Features
            .Select(f => $"{f.Name}: {string.Join(", ", f.RoleAccess.Select(kv => $"{kv.Key}={kv.Value}"))}")
            .Should().Equal(
                "Legal documents: Board=Allowed, Admin=Allowed");
    }
}
