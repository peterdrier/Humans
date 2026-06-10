using System.Net;
using AwesomeAssertions;
using Humans.Integration.Tests.Infrastructure;
using Xunit;

namespace Humans.Integration.Tests;

public class AnonymousAccessTests(HumansWebApplicationFactory factory) : IntegrationTestBase(factory)
{
    [HumansFact(Timeout = 30000)]
    public async Task Homepage_IsAccessibleAnonymously()
    {
        var response = await Client.GetAsync("/");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [HumansFact(Timeout = 30000)]
    public async Task AboutPage_IsAccessibleAnonymously()
    {
        var response = await Client.GetAsync("/About");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [HumansFact(Timeout = 30000)]
    public async Task PrivacyPage_IsAccessibleAnonymously()
    {
        var response = await Client.GetAsync("/Home/Privacy");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [HumansFact(Timeout = 30000)]
    public async Task TeamsPage_IsAccessibleAnonymously()
    {
        var response = await Client.GetAsync("/Teams");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [HumansFact(Timeout = 30000)]
    public async Task ICalFeed_UnknownUserAndToken_Returns404NotLoginRedirect()
    {
        // Proves the route binds with the .ics literal suffix and that
        // AllowAnonymous holds — a bad token must 404, never bounce to login.
        var response = await Client.GetAsync($"/api/ical/{Guid.NewGuid()}/{Guid.NewGuid()}.ics");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [HumansTheory(Timeout = 30000)]
    [InlineData("/Profile")]
    [InlineData("/Admin")]
    [InlineData("/Consent")]
    public async Task ProtectedEndpoint_RedirectsToLogin_WhenAnonymous(string path)
    {
        var response = await Client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.PathAndQuery.Should().StartWith("/Account/Login");
    }
}
