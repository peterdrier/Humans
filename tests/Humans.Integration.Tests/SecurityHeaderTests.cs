using AwesomeAssertions;
using Humans.Integration.Tests.Infrastructure;
using Xunit;

namespace Humans.Integration.Tests;

public class SecurityHeaderTests : IntegrationTestBase
{
    public SecurityHeaderTests(HumansWebApplicationFactory factory) : base(factory) { }

    [HumansFact]
    public async Task Response_ContainsXFrameOptionsDeny()
    {
        var response = await Client.GetAsync("/");

        response.Headers.TryGetValues("X-Frame-Options", out var values).Should().BeTrue();
        values.Should().ContainSingle().Which.Should().Be("DENY");
    }

    [HumansFact]
    public async Task Response_ContainsXContentTypeOptionsNosniff()
    {
        var response = await Client.GetAsync("/");

        response.Headers.TryGetValues("X-Content-Type-Options", out var values).Should().BeTrue();
        values.Should().ContainSingle().Which.Should().Be("nosniff");
    }

    [HumansFact]
    public async Task Response_ContainsContentSecurityPolicy()
    {
        var response = await Client.GetAsync("/");

        response.Headers.TryGetValues("Content-Security-Policy", out var values).Should().BeTrue();
        var csp = string.Join("; ", values!);
        csp.Should().Contain("default-src 'self'");
    }

    [HumansFact]
    public async Task Response_CspContainsNonce()
    {
        var response = await Client.GetAsync("/");

        response.Headers.TryGetValues("Content-Security-Policy", out var values).Should().BeTrue();
        var csp = string.Join("; ", values!);
        csp.Should().Contain("'nonce-");
    }

    [HumansFact]
    public async Task Response_ContainsReferrerPolicy()
    {
        var response = await Client.GetAsync("/");

        response.Headers.TryGetValues("Referrer-Policy", out var values).Should().BeTrue();
        values.Should().ContainSingle().Which.Should().Be("strict-origin-when-cross-origin");
    }

    [HumansFact]
    public async Task Response_ContainsPermissionsPolicy()
    {
        var response = await Client.GetAsync("/");

        response.Headers.TryGetValues("Permissions-Policy", out var values).Should().BeTrue();
        values.Should().ContainSingle().Which.Should().Contain("geolocation=()");
    }
}
