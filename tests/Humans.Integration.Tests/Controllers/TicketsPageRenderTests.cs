using System.Net;
using AwesomeAssertions;
using Humans.Integration.Tests.Infrastructure;

namespace Humans.Integration.Tests.Controllers;

/// <summary>Renders every Tickets page through the real app.</summary>
/// <remarks>
/// <para>
/// Failures render as a 200 with degraded content: unbound tags ship as markup, a mis-bound key renders as its name,
/// and only the Spanish request proves the satellite assemblies ship. <c>HumanSummary</c> is invoked by name and
/// throws instead. The admin controllers name views by absolute path, so moving those folders compiles and then 500s.
/// </para>
/// <para>
/// The database is empty: every admin page renders its no-data branch, and the transfer wizard renders all the copy
/// with no tickets held.
/// </para>
/// </remarks>
public class TicketsPageRenderTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    private static (string Url, string Copy)[] AdminPages =>
    [
        ("/Tickets", "Tickets"),
        ("/Tickets/Orders", "Ticket Orders"),
        ("/Tickets/Attendees", "Ticket Attendees"),
        ("/Tickets/Codes", "Code Tracking"),
        ("/Tickets/SalesAggregates", "Aggregates"),
        ("/Tickets/WhoHasntBought", "Humans &amp; Tickets"),
        ("/Tickets/Participation/Backfill", "Backfill Participation Data"),
        ("/Tickets/Admin/Transfers", "Ticket transfer requests"),
        ("/Tickets/Admin/Onsite", "Who's Onsite"),
        // /Tickets/Admin/Contacts is left out: it throws by design until a vendor sync has run. Its view is covered by
        // TicketsContactsAdminControllerTests.
    ];

    [HumansFact(Timeout = 180000)]
    public async Task Every_tickets_admin_page_renders_with_tag_helpers_applied()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        foreach (var (url, copy) in AdminPages)
        {
            var response = await Client.GetAsync(url, ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {url} must render");

            var html = await response.Content.ReadAsStringAsync(ct);
            html.Should().Contain(copy, $"GET {url} must render its own copy");

            // ReSharper can rewrite <vc:name> to <name-view-component>, which also renders inert.
            html.Should().NotContain("-view-component", $"GET {url} has a rewritten vc tag");

            html.Should().NotContain("TicketTransfer_", $"GET {url} rendered a raw resource key");
        }
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_dashboard_renders_the_access_matrix_tag_helper()
    {
        // An unbound <vc:> ships as inert markup, so the emitted modal id is the proof, not the 200.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var html = await (await Client.GetAsync("/Tickets", ct)).Content.ReadAsStringAsync(ct);

        html.Should().Contain("sectionHelp-Tickets", "the access-matrix widget must bind and render from a section view");
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_transfer_wizard_renders_the_sections_own_resource_set_and_ticket_stub()
    {
        // Every Localizer call in the section is on this page, and it is the only section view reaching <vc:ticket-stub>.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);

        var response = await Client.GetAsync("/Tickets/Transfers", ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await response.Content.ReadAsStringAsync(ct);
        // The no-tickets branch. The apostrophe in NoTickets is typographic and encodes to an entity, so the check
        // stays on an ASCII run.
        html.Should().Contain("Transfer a ticket");                    // Tickets_TicketTransfer_Title
        html.Should().Contain("have any tickets to transfer");         // Tickets_TicketTransfer_NoTickets
        html.Should().Contain("Your transfer requests");               // Tickets_TicketTransfer_YourRequests
        html.Should().Contain("Back");                                 // Common_Back, from SharedResource
        html.Should().NotContain("TicketTransfer_", "a carved key rendered as its own name");
        html.Should().NotContain("Common_", "a shared key rendered as its own name");
    }

    [HumansFact(Timeout = 120000)]
    public async Task A_volunteer_cannot_reach_the_ticket_admin_pages()
    {
        // Cookie auth redirects an authenticated-but-unauthorized request to AccessDeniedPath: a 302, not a 403.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);

        foreach (var url in new[] { "/Tickets", "/Tickets/Orders", "/Tickets/Admin/Transfers" })
        {
            var response = await Client.GetAsync(url, ct);
            response.StatusCode.Should().Be(HttpStatusCode.Found, $"GET {url} must be gated");
            response.Headers.Location?.AbsolutePath.Should().Be("/Account/AccessDenied",
                because: $"GET {url} must be denied, not sent to sign in");
        }
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_transfer_wizard_renders_in_spanish_from_the_sections_satellite_assemblies()
    {
        // The neutral set is embedded in the main assembly and fallback is silent, so only Spanish proves the
        // satellites. HtmlEncoder escapes non-ASCII, so the checks stay on ASCII runs.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);

        // Accept-Language doesn't reach a signed-in user: the PreferredLanguage provider short-circuits the chain. Switch
        // the way the UI does.
        var switcherPage = await (await Client.GetAsync("/Tickets/Transfers", ct)).Content.ReadAsStringAsync(ct);
        var token = ExtractAntiForgeryToken(switcherPage);
        token.Should().NotBeNullOrEmpty();
        await Client.PostAsync("/Language/SetLanguage", new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("__RequestVerificationToken", token),
                new KeyValuePair<string, string>("culture", "es"),
            ]), ct);

        var response = await Client.GetAsync("/Tickets/Transfers", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync(ct);

        html.Should().Contain("Transferir una entrada");         // Tickets_TicketTransfer_Title
        html.Should().Contain("Tus solicitudes de transferencia"); // Tickets_TicketTransfer_YourRequests
        html.Should().NotContain("Transfer a ticket");
    }

    [HumansFact(Timeout = 120000)]
    public async Task Shell_renders_the_sections_ticket_stub_component()
    {
        // The Shell half, which fails separately: an @using doesn't import the tag helper, so Shell needs its own
        // @addTagHelper *, Humans.Tickets or the stubs ship as inert markup. WidgetGallery renders three variants from
        // inline data, including the void and early-entry branches.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var response = await Client.GetAsync("/Debug/WidgetGallery", ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await response.Content.ReadAsStringAsync(ct);
        html.Should().Contain("Sample Human", "the stub card renders its attendee name");
    }

    private static string? ExtractAntiForgeryToken(string html)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            html,
            "name=\"__RequestVerificationToken\"[^>]{0,200}value=\"(?<token>[^\"]+)\"",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
                | System.Text.RegularExpressions.RegexOptions.ExplicitCapture,
            TimeSpan.FromSeconds(2));
        return match.Success ? match.Groups["token"].Value : null;
    }
}
