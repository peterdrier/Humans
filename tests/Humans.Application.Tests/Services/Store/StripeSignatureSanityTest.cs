using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using Stripe;

namespace Humans.Application.Tests.Services.Store;

/// <summary>
/// Pure sanity check that our test-side webhook signing helper produces a header
/// that Stripe.NET's EventUtility.ConstructEvent accepts. If this fails, the
/// integration tests' "valid signature" assertions are testing the wrong thing.
/// </summary>
public class StripeSignatureSanityTest
{
    [HumansFact]
    public void Roundtrip_signed_payload_passes_EventUtility()
    {
        const string secret = "whsec_test_humans_integration_secret_do_not_use_in_prod";
        const string payload = """
        {"id":"evt_test_x","object":"event","type":"customer.created","data":{"object":{}}}
        """;

        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signedPayload = $"{ts.ToString(CultureInfo.InvariantCulture)}.{payload}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(signedPayload));
        var hex = Convert.ToHexString(hash).ToLowerInvariant();
        var header = $"t={ts.ToString(CultureInfo.InvariantCulture)},v1={hex}";

        var act = () => EventUtility.ConstructEvent(payload, header, secret, throwOnApiVersionMismatch: false);
        act.Should().NotThrow();
    }
}
