using System.Net;
using AwesomeAssertions;
using Humans.Integration.Tests.Infrastructure;

namespace Humans.Integration.Tests.Controllers;

/// <summary>
/// Renders the member dashboard (<c>/</c>) through the real app, as the standing check for
/// the Shell decoupling that moved its shift/consent/governance content behind the
/// section-contributed member-dashboard chrome slot (nobodies-collective/Humans#1091).
/// </summary>
/// <remarks>
/// The failure this guards is a <b>200 with degraded content</b>: a section RCL does not
/// inherit the host's <c>Views/_ViewImports.cshtml</c>, so a missing <c>@@using</c> or
/// <c>@@addTagHelper</c> in Shifts' or Governance's own imports ships an unrendered
/// <c>&lt;vc:…&gt;</c> tag as inert literal markup, or a moved localization key that missed
/// its section's resx renders as its own raw key name — both on a green build and a green
/// 200. Status-code assertions alone cannot see either.
/// </remarks>
public class HomePageRenderTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    /// <summary>Prefixes the carve moved off <c>SharedResource</c> into Shifts'/Governance's own sets.</summary>
    private static readonly string[] CarvedPrefixes =
        ["GetInvolved_", "Dashboard_SlotsSingular", "Dashboard_SlotsPlural", "Dashboard_GetInvolved",
         "Dashboard_AgreementSingular", "Dashboard_AgreementPlural", "Dashboard_GovernanceCardDescription"];

    private static void AssertRenderedCleanly(string html, string what)
    {
        html.Should().NotContain("<vc:", $"{what} left a view-component tag unrendered");
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

        // Shifts' DashboardShifts "Get involved" tri-card — renders whenever the signed-in
        // human is a volunteer member, independent of whether an event is active.
        html.Should().Contain("Get involved", "Shifts' DashboardShifts component must bind and render its tri-card row");

        // Governance's GovernanceApplicationsTile — same volunteer-member gate, its own resx.
        html.Should().Contain(
            "Assemblies, elections, and the serious stuff.",
            "Governance's GovernanceApplicationsTile component must bind and render");

        AssertRenderedCleanly(html, "GET / (dashboard)");
    }
}
