using AwesomeAssertions;
using Humans.Infrastructure.Services;
using Xunit;

namespace Humans.Application.Tests.Services.Store;

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
}
