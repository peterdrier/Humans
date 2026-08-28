using System.Net;
using AwesomeAssertions;
using Humans.Integration.Tests.Infrastructure;
using Xunit;

namespace Humans.Integration.Tests.Controllers;

/// <summary>
/// Renders the Tour section's public page. Tour is the first section created directly in
/// src/Sections (never lived in Shell), so this is the standing proof that a from-scratch
/// section RCL routes, resolves a layout (Tour ships its own _TourLayout), and renders —
/// the failure modes are a 404 (controller not discovered) or a 200 with literal markup
/// (missing _ViewImports line).
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
    }

    [HumansFact(Timeout = 60000)]
    public async Task Anonymous_visitors_can_reach_Tour_from_the_nav_and_the_Welcome_page()
    {
        var ct = TestContext.Current.CancellationToken;

        var welcomeHtml = await (await Client.GetAsync("/Welcome", ct)).Content.ReadAsStringAsync(ct);
        welcomeHtml.Should().Contain("href=\"/Tour\"",
            because: "the Welcome landing is where anonymous visitors arrive (no-orphan-pages rule)");

        var tourHtml = await (await Client.GetAsync("/Tour", ct)).Content.ReadAsStringAsync(ct);
        tourHtml.Should().Contain("tour-header",
            because: "the landing page uses its own layout, whose fixed header bar is the chrome");
        tourHtml.Should().Contain("href=\"/\"",
            because: "the header bar must offer the way back into Humans (no-orphan-pages rule)");
    }

    [HumansFact(Timeout = 60000)]
    public async Task Signed_in_members_reach_Tour_from_a_dashboard_tile_not_the_nav()
    {
        // The signed-in top nav is too busy for a Tour slot (Peter, 2026-08-13); members get
        // a dashboard action card instead.
        var ct = TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var homeHtml = await (await Client.GetAsync("/", ct)).Content.ReadAsStringAsync(ct);
        homeHtml.Should().Contain("href=\"/Tour\"", because: "the dashboard Tour tile is the member-facing entry");
    }
}
