using System.Net;
using AwesomeAssertions;
using Humans.Integration.Tests.Infrastructure;
using Xunit;

namespace Humans.Integration.Tests;

public class AnonymousAccessTests : IntegrationTestBase
{
    public AnonymousAccessTests(HumansWebApplicationFactory factory) : base(factory) { }

    [Fact]
    public async Task Homepage_IsAccessibleAnonymously()
    {
        var response = await Client.GetAsync("/");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AboutPage_IsAccessibleAnonymously()
    {
        var response = await Client.GetAsync("/About");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PrivacyPage_IsAccessibleAnonymously()
    {
        var response = await Client.GetAsync("/Home/Privacy");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TeamsPage_IsAccessibleAnonymously()
    {
        var response = await Client.GetAsync("/Teams");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
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
