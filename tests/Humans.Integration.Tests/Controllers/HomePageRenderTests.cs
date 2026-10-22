using System.Net;
using AwesomeAssertions;
using Humans.Integration.Tests.Infrastructure;

namespace Humans.Integration.Tests.Controllers;

/// <summary>Renders the member dashboard (<c>/</c>) through the real app.</summary>
/// <remarks>
/// Its shift, consent and governance content comes from sections through the member-dashboard slot. A missing
/// <c>@@using</c> or <c>@@addTagHelper</c> in a section's imports ships an inert <c>&lt;vc:…&gt;</c>, and a key that
/// missed its section's resx renders as its own name, both behind a 200.
/// </remarks>
public class HomePageRenderTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    /// <summary>Prefixes that live in Shifts' and Governance's own sets, not <c>SharedResource</c>.</summary>
    private static readonly string[] CarvedPrefixes =
        ["GetInvolved_", "Dashboard_SlotsSingular", "Dashboard_SlotsPlural", "Dashboard_GetInvolved",
         "Governance_Dashboard_AgreementSingular", "Governance_Dashboard_AgreementPlural", "Governance_Dashboard_GovernanceCardDescription"];

    private static void AssertRenderedCleanly(string html, string what)
    {
        html.Should().NotContain("-view-component", $"{what} has a renamed view-component element");
        foreach (var prefix in CarvedPrefixes)
        {
            html.Should().NotContain(prefix, $"{what} rendered a raw {prefix}* resource key");
        }
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_dashboard_renders_every_section_contributed_card()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);

        var response = await Client.GetAsync("/", ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync(ct);

        // Renders for any volunteer member, active event or not.
        html.Should().Contain("Get involved", "Shifts' DashboardShifts component must bind and render its tri-card row");

        // Same volunteer-member gate, its own resx.
        html.Should().Contain(
            "Assemblies, elections, and the serious stuff.",
            "Governance's GovernanceApplicationsTile component must bind and render");

        AssertRenderedCleanly(html, "GET / (dashboard)");
    }
}
