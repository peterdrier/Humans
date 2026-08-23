using AwesomeAssertions;
using Humans.Expenses.Domain;
using Humans.Expenses.Services;
using NodaTime;
using Xunit;

namespace Humans.Expenses.Tests.Services;

/// <summary>
/// The matcher is where nobodies-collective/Humans#1030's two structural guarantees live, so they
/// are asserted here — on the pure function, with no DB, no HTTP and no screen — rather than only
/// through the UI where they would rot silently:
/// <list type="bullet">
/// <item>a second document for an already-invoiced commitment is flagged, never linked (AC4);</item>
/// <item>a tie is never broken by a guess — it goes to the review queue (AC6).</item>
/// </list>
/// </summary>
public class VendorCommitmentMatcherTests
{
    private static readonly LocalDate Day = new(2026, 6, 1);

    private static MatchableDocument Doc(
        string id, decimal total, string contact = "TOI TOI", int dayOffset = 0,
        string currency = "EUR") =>
        new(id, $"PC-{id}", contact, Day.PlusDays(dayOffset), total, currency);

    [HumansFact]
    public void NoDocumentAtTheCommittedAmount_IsNoMatch()
    {
        var outcome = VendorCommitmentMatcher.Match(
            100m, "TOI TOI", "EUR", alreadyInvoiced: false,
            [Doc("a", 99.99m), Doc("b", 100.01m)]);

        outcome.Decision.Should().Be(VendorCommitmentMatchDecision.NoMatch);
        outcome.ForReview.Should().BeEmpty();
    }

    [HumansFact]
    public void SingleExactAmountAndVendor_Links()
    {
        var outcome = VendorCommitmentMatcher.Match(
            69_398.34m, "TOI TOI", "EUR", alreadyInvoiced: false,
            [Doc("a", 69_398.34m), Doc("b", 1_000m)]);

        outcome.Decision.Should().Be(VendorCommitmentMatchDecision.Link);
        outcome.Linked!.Id.Should().Be("a");
        outcome.ForReview.Should().BeEmpty();
    }

    [HumansFact]
    public void AmountMatchesButVendorDoesNot_IsNoMatch()
    {
        var outcome = VendorCommitmentMatcher.Match(
            500m, "Talleres Fandos", "EUR", alreadyInvoiced: false, [Doc("a", 500m, "Repsol")]);

        outcome.Decision.Should().Be(VendorCommitmentMatchDecision.NoMatch);
    }

    // AC6. Two documents fit equally; the only ways to separate them (date proximity, document
    // order) are guesses, so the matcher must produce neither a link nor a preference.
    [HumansFact]
    public void TwoEqualFits_GoToReviewAsAmbiguous_AndNeitherIsLinked()
    {
        var outcome = VendorCommitmentMatcher.Match(
            4_024.30m, "Talleres Fandos", "EUR", alreadyInvoiced: false,
            [
                Doc("a", 4_024.30m, "Talleres Fandos", dayOffset: 0),
                Doc("b", 4_024.30m, "Talleres Fandos SL", dayOffset: 40),
            ]);

        outcome.Decision.Should().Be(VendorCommitmentMatchDecision.Review);
        outcome.ReviewKind.Should().Be(VendorCommitmentMatchKind.Ambiguous);
        outcome.Linked.Should().BeNull();
        outcome.ForReview.Select(d => d.Id).Should().BeEquivalentTo(["a", "b"]);
    }

    // AC6 again, from the other side: the nearer document must NOT win. This is the assertion that
    // catches someone "improving" the matcher with a date tie-break later.
    [HumansFact]
    public void DateProximityIsNeverUsedToBreakATie()
    {
        var outcome = VendorCommitmentMatcher.Match(
            250m, "Covey", "EUR", alreadyInvoiced: false,
            [Doc("near", 250m, "Covey", dayOffset: 1), Doc("far", 250m, "Covey", dayOffset: 300)]);

        outcome.Decision.Should().Be(VendorCommitmentMatchDecision.Review);
        outcome.Linked.Should().BeNull();
    }

    // AC4 — the TOI TOI failure. The commitment is already invoiced, so a further exact-amount
    // document is a suspected second booking of the same cost and is flagged, not linked.
    [HumansFact]
    public void SecondDocumentForAnInvoicedCommitment_IsFlaggedAsDuplicate_NotLinked()
    {
        var outcome = VendorCommitmentMatcher.Match(
            69_398.34m, "TOI TOI", "EUR", alreadyInvoiced: true, [Doc("second", 69_398.34m)]);

        outcome.Decision.Should().Be(VendorCommitmentMatchDecision.Review);
        outcome.ReviewKind.Should().Be(VendorCommitmentMatchKind.Duplicate);
        outcome.Linked.Should().BeNull();
        outcome.ForReview.Should().ContainSingle().Which.Id.Should().Be("second");
    }

    [HumansFact]
    public void InvoicedCommitmentWithNoFurtherMatch_StaysQuiet()
    {
        var outcome = VendorCommitmentMatcher.Match(
            69_398.34m, "TOI TOI", "EUR", alreadyInvoiced: true, [Doc("other", 12m)]);

        outcome.Decision.Should().Be(VendorCommitmentMatchDecision.NoMatch);
    }

    // Equal nominal totals in different currencies are different sums of money, so the vendor and
    // the amount both agreeing is a coincidence rather than a fit.
    [HumansFact]
    public void SameAmountInAnotherCurrency_IsNoMatch()
    {
        var outcome = VendorCommitmentMatcher.Match(
            1_000m, "TOI TOI", "EUR", alreadyInvoiced: false,
            [Doc("usd", 1_000m, currency: "USD")]);

        outcome.Decision.Should().Be(VendorCommitmentMatchDecision.NoMatch);
    }

    // Holded reports the currency lowercase ("eur"); the commitment stores it uppercase.
    [HumansFact]
    public void CurrencyIsComparedCaseInsensitively()
    {
        var outcome = VendorCommitmentMatcher.Match(
            1_000m, "TOI TOI", "EUR", alreadyInvoiced: false,
            [Doc("a", 1_000m, currency: "eur")]);

        outcome.Decision.Should().Be(VendorCommitmentMatchDecision.Link);
    }

    // The Cruz Roja duplicate hid for months behind exactly this name mismatch.
    [HumansTheory]
    [InlineData("Cruz Roja", "Cruz Roja Española", true)]
    [InlineData("TOI TOI", "TOI TOI, S.L.", true)]
    [InlineData("Modulos Orly", "Módulos Orly", true)]
    [InlineData("Repsol", "Morillo", false)]
    [InlineData("Alba", "Alerta y Control", false)]
    public void VendorMatches_TreatsTheSameVendorAsTheSameVendor(string a, string b, bool expected) =>
        VendorCommitmentMatcher.VendorMatches(a, b).Should().Be(expected);

    // A two- or three-letter normalized name would otherwise contain-match half the register.
    [HumansTheory]
    [InlineData("SA", "Sabadell SA")]
    [InlineData("SL", "Talleres SL")]
    public void VendorMatches_RefusesToContainMatchOnATooShortName(string a, string b) =>
        VendorCommitmentMatcher.VendorMatches(a, b).Should().BeFalse();

    [HumansFact]
    public void VendorMatches_IsFalseForBlankNames()
    {
        VendorCommitmentMatcher.VendorMatches("", "Repsol").Should().BeFalse();
        VendorCommitmentMatcher.VendorMatches("Repsol", null).Should().BeFalse();
    }
}
