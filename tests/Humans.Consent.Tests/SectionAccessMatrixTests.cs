using Humans.Base.Interfaces;
using AwesomeAssertions;
using Humans.Base.Models;

namespace Humans.Consent.Tests;

public class SectionAccessMatrixTests
{
    private static AccessMatrixData Matrix() =>
        ((ISectionAccessMatrix)new Section()).AccessMatrices.Single(m => string.Equals(m.Key, "ConsentReview", StringComparison.Ordinal));

    [HumansFact]
    public void AccessMatrix_ConsentReview_UsesPageKey()
    {
        var matrix = Matrix();

        matrix.Key.Should().Be("ConsentReview");
        matrix.SectionName.Should().Be("Consent Review");
        matrix.Order.Should().Be(50);
        matrix.Roles.Should().Equal("ConsentAdmin", "Board");
    }

    /// <summary>
    /// The Consent Coordinator clears and flags but has no Board vote. That negative is
    /// the point of the table, so it is pinned literally.
    /// </summary>
    [HumansFact]
    public void AccessMatrix_MatchesConsentReviewPin()
    {
        Matrix().Features
            .Select(f => $"{f.Name}: {string.Join(", ", f.RoleAccess.Select(kv => $"{kv.Key}={kv.Value}"))}")
            .Should().Equal(
                "View review queue: ConsentAdmin=Allowed, Board=Allowed",
                "Clear consent checks: ConsentAdmin=Allowed, Board=Allowed",
                "Flag / reject signup: ConsentAdmin=Allowed, Board=Allowed",
                "Board voting: ConsentAdmin=Denied, Board=Allowed");
    }

    [HumansFact]
    public void AccessMatrix_MatchesLegalDocumentsPin()
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
