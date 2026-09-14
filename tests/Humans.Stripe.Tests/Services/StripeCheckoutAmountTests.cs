using AwesomeAssertions;
using Humans.Stripe.Services;
using Xunit;

namespace Humans.Stripe.Tests.Services;

/// <summary>
/// Pure conversion helper for EUR → Stripe minor units (cents) used by Checkout Session creation.
/// Stripe expects a long integer in the smallest currency unit; the bug-prone bit is rounding
/// behavior on the boundary (e.g. 19.995 → 2000, not 1999), so we lock that in here.
/// </summary>
public class StripeCheckoutAmountTests
{
    [HumansTheory]
    [InlineData(0, 0L)]
    [InlineData(1, 100L)]
    [InlineData(0.01, 1L)]
    [InlineData(19.99, 1999L)]
    [InlineData(19.995, 2000L)]   // half-cent rounds away from zero
    [InlineData(19.994, 1999L)]
    [InlineData(123456.78, 12345678L)]
    public void Converts_eur_to_stripe_minor_units(decimal eur, long expectedMinorUnits)
    {
        StripeService.ToStripeMinorUnits(eur).Should().Be(expectedMinorUnits);
    }

    /// <summary>
    /// The inverse. Dividing a long by 100m is exact, so there is no rounding rule to pin on
    /// this side — what matters is that it stays decimal. Both live fee paths
    /// (GetPaymentDetailsAsync's fee sum, MapSession's AmountEur) run through here, and a
    /// change to double would silently skew every fee figure the finance pages show.
    /// </summary>
    [HumansTheory]
    [InlineData(0L, 0)]
    [InlineData(1L, 0.01)]
    [InlineData(1999L, 19.99)]
    [InlineData(-250L, -2.50)]
    [InlineData(12345678L, 123456.78)]
    public void Converts_stripe_minor_units_back_to_eur(long minorUnits, decimal expectedEur)
    {
        StripeService.FromStripeMinorUnits(minorUnits).Should().Be(expectedEur);
    }

    [HumansTheory]
    [InlineData(0)]
    [InlineData(0.01)]
    [InlineData(19.99)]
    [InlineData(123456.78)]
    public void Round_trips_a_representable_amount_without_loss(decimal eur)
    {
        StripeService.FromStripeMinorUnits(StripeService.ToStripeMinorUnits(eur)).Should().Be(eur);
    }
}
