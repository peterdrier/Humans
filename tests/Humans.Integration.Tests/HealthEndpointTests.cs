using System.Net;
using AwesomeAssertions;
using Humans.Integration.Tests.Infrastructure;
using Xunit;

namespace Humans.Integration.Tests;

public class HealthEndpointTests : IntegrationTestBase
{
    public HealthEndpointTests(HumansWebApplicationFactory factory) : base(factory) { }

    [HumansFact]
    public async Task LivenessEndpoint_ReturnsOk()
    {
        var response = await Client.GetAsync("/health/live");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [HumansFact]
    public async Task ReadinessEndpoint_ReturnsResponse()
    {
        var response = await Client.GetAsync("/health/ready");

        // Readiness checks external dependencies (SMTP, GitHub, Google Workspace).
        // In the test environment these are unavailable, so the endpoint returns 503.
        // The important thing is that the endpoint exists and responds.
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable);
    }

    [HumansFact]
    public async Task DetailedHealthEndpoint_ReturnsJsonWithStatus()
    {
        var response = await Client.GetAsync("/health");

        // The detailed endpoint may return 503 when external health checks fail,
        // but it should always return JSON with status information.
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("\"status\"");
        content.Should().Contain("\"results\"");
    }
}
