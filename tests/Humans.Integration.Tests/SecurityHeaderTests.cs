using AwesomeAssertions;
using Humans.Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Humans.Integration.Tests;

public class SecurityHeaderTests(HumansWebApplicationFactory factory) : IntegrationTestBase(factory)
{
    [HumansFact]
    public async Task Response_ContainsXFrameOptionsDeny()
    {
        var response = await Client.GetAsync("/", Xunit.TestContext.Current.CancellationToken);

        response.Headers.TryGetValues("X-Frame-Options", out var values).Should().BeTrue();
        values.Should().ContainSingle().Which.Should().Be("DENY");
    }

    [HumansFact]
    public async Task Response_ContainsXContentTypeOptionsNosniff()
    {
        var response = await Client.GetAsync("/", Xunit.TestContext.Current.CancellationToken);

        response.Headers.TryGetValues("X-Content-Type-Options", out var values).Should().BeTrue();
        values.Should().ContainSingle().Which.Should().Be("nosniff");
    }

    [HumansFact]
    public async Task Response_ContainsContentSecurityPolicy()
    {
        var response = await Client.GetAsync("/", Xunit.TestContext.Current.CancellationToken);

        response.Headers.TryGetValues("Content-Security-Policy", out var values).Should().BeTrue();
        var csp = string.Join("; ", values!);
        csp.Should().Contain("default-src 'self'");
    }

    [HumansFact]
    public async Task Response_CspContainsNonce()
    {
        var response = await Client.GetAsync("/", Xunit.TestContext.Current.CancellationToken);

        response.Headers.TryGetValues("Content-Security-Policy", out var values).Should().BeTrue();
        var csp = string.Join("; ", values!);
        csp.Should().Contain("'nonce-");
    }

    [HumansFact]
    public async Task Response_ContainsReferrerPolicy()
    {
        var response = await Client.GetAsync("/", Xunit.TestContext.Current.CancellationToken);

        response.Headers.TryGetValues("Referrer-Policy", out var values).Should().BeTrue();
        values.Should().ContainSingle().Which.Should().Be("strict-origin-when-cross-origin");
    }

    [HumansFact]
    public async Task Response_ContainsPermissionsPolicy()
    {
        var response = await Client.GetAsync("/", Xunit.TestContext.Current.CancellationToken);

        response.Headers.TryGetValues("Permissions-Policy", out var values).Should().BeTrue();
        values.Should().ContainSingle().Which.Should().Contain("geolocation=()");
    }

    // The auth-cookie policy from nobodies-collective#925 lives here rather than in its own
    // class: every test class owns an IClassFixture<HumansWebApplicationFactory>, and each
    // factory starts its own Testcontainers Postgres. Reusing this class's fixture keeps the
    // suite at 14 parallel containers instead of 15 — worth doing for two assertions, since
    // several tests already run 26-29s against HumansFact's 30s default timeout.

    [HumansFact]
    public async Task SignIn_IssuesPersistentCookie_NotSessionCookie()
    {
        var response = await Client.GetAsync("/dev/login/volunteer", Xunit.TestContext.Current.CancellationToken);

        response.Headers.TryGetValues("Set-Cookie", out var setCookies).Should().BeTrue();
        var raw = setCookies!.Should()
            .ContainSingle(c => c.StartsWith(".AspNetCore.Identity.Application", StringComparison.Ordinal))
            .Subject;

        // Expires is the whole point: a session cookie omits it and dies with the browser,
        // which is what forced a re-login on essentially every visit.
        SetCookieHeaderValue.Parse(raw).Expires.Should().NotBeNull();
    }

    [HumansFact]
    public void ApplicationCookie_UsesFourteenDaySlidingWindow()
    {
        var options = Factory.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);

        options.ExpireTimeSpan.Should().Be(TimeSpan.FromDays(14));
        options.SlidingExpiration.Should().BeTrue();
    }
}
