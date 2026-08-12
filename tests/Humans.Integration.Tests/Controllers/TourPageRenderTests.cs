using System.Net;
using AwesomeAssertions;
using Humans.Integration.Tests.Infrastructure;
using Xunit;

namespace Humans.Integration.Tests.Controllers;

/// <summary>
/// Renders the Tour section's public page. Tour is the first section created directly in
/// src/Sections (never lived in Shell), so this is the standing proof that a from-scratch
/// section RCL routes, resolves the host layout, and renders — the failure modes are a 404
/// (controller not discovered) or a 200 with literal markup (missing _ViewImports line).
/// </summary>
public class TourPageRenderTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    [HumansFact(Timeout = 60000)]
    public async Task Tour_page_renders_anonymously()
    {
        var response = await Client.GetAsync("/Tour", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        html.Should().Contain("What is Humans");
        html.Should().Contain("People");
        html.Should().Contain("Organize");
        html.Should().Contain("Money");
        html.Should().Contain("Govern");
        html.Should().Contain("Communicate");
        html.Should().Contain("Humans for your burn");
        html.Should().Contain("href=\"/About\"");
        html.Should().NotContain("<vc:", because: "an unresolved view component tag renders as inert literal markup");
    }
}
