using AwesomeAssertions;
using Humans.Pleo.Domain;
using Xunit;

namespace Humans.Pleo.Tests;

public sealed class PleoEntryRulesTests
{
    [HumansTheory]
    [InlineData("WALLET", "LOAD", 3000000, "30000.00")]
    [InlineData("CARD_PURCHASE", "IN_STORE", 1250, "-12.50")]
    [InlineData("CARD_PURCHASE", "ONLINE", 1250, "-12.50")]
    [InlineData("PLEO_INVOICE", "PLEO_INVOICE", 7200, "-72.00")]
    [InlineData("OUT_OF_POCKET", null, 5698, "-56.98")]
    public void Sign_NoBillValue_ComesFromFamily(
        string family, string? subFamily, long minors, decimal expected) =>
        PleoEntryRules.SignedEur(family, subFamily, "EUR", minors, null, null).Should().Be(expected);

    [HumansFact]
    public void Classify_UnclassifiedFamily_IsNotGuessed() =>
        PleoEntryRules.SignedEur("CARD_PURCHASE", "ATM", "EUR", 1000, null, null).Should().BeNull();

    [HumansFact]
    public void Refund_WithBillValue_TakesItsSign() =>
        PleoEntryRules.SignedEur("CARD_PURCHASE", "ONLINE", "EUR", 8712, "EUR", 8712).Should().Be(87.12m);

    [HumansFact]
    public void ForeignPurchase_WithBillValue_UsesIt() =>
        PleoEntryRules.SignedEur("CARD_PURCHASE", "ONLINE", "USD", 4800, "EUR", -4365).Should().Be(-43.65m);

    [HumansFact]
    public void ForeignPurchase_NoBillValue_HasNoEurAmount() =>
        PleoEntryRules.SignedEur("CARD_PURCHASE", "ONLINE", "USD", 4800, null, null).Should().BeNull();

    [HumansTheory]
    [InlineData("WALLET", "UNLOAD")]
    [InlineData("CARD_PURCHASE", null)]
    [InlineData("REIMBURSEMENT", null)]
    public void Classify_UnknownPair_IsUnclassified(string family, string? subFamily) =>
        PleoEntryRules.KindOf(family, subFamily).Should().Be(PleoEntryKind.Unclassified);
}
