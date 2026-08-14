using System.Net;
using AwesomeAssertions;
using Humans.Integration.Tests.Infrastructure;

namespace Humans.Integration.Tests.Controllers;

/// <summary>
/// Renders every Tickets page through the real app, as the standing form of the §15 step 12
/// check for the section's move into <c>src/Sections/Humans.Tickets</c>
/// (nobodies-collective/Humans#866, G5).
/// </summary>
/// <remarks>
/// <para>
/// The failure modes a G5 move introduces all render as a <b>200 with degraded content</b>, so
/// "the page loads" is not the assertion:
/// </para>
/// <list type="number">
/// <item><description>
/// A section RCL does not inherit the host's <c>Views/_ViewImports.cshtml</c> — a missing
/// <c>@@using</c> or <c>@@addTagHelper</c> ships literal markup with a green build, and an
/// unrendered <c>&lt;vc:…&gt;</c> element is inert text the browser simply drops. Tickets is
/// the first section to ship a <c>&lt;vc:&gt;</c> component of its <em>own</em> (the ticket
/// stub, public under <c>Contracts/</c>), alongside <c>&lt;vc:access-matrix&gt;</c> bound
/// through <c>@@addTagHelper *, Humans.UI</c> and <c>HumanSummary</c>, still Shell's and
/// invoked by name — the latter <b>throws</b> when it fails to resolve rather than degrading.
/// </description></item>
/// <item><description>
/// The resx carve moved the 25 <c>TicketTransfer_*</c> keys out of <c>SharedResource</c>. A
/// key the carve missed, or a call site bound to the wrong set, renders the raw key — in
/// every language, with no error. The Spanish request is the only thing that proves the
/// section RCL's satellite assemblies reach the host's probing path at all.
/// </description></item>
/// <item><description>
/// The two admin controllers that name their view by absolute path
/// (<c>~/Views/Tickets/Admin/…</c>) pin the folder layout: an RCL's compiled view paths are
/// project-relative, so tidying <c>Views/Tickets/Admin/</c> into the <c>Views/&lt;Controller&gt;/</c>
/// shape compiles and then 500s on request.
/// </description></item>
/// </list>
/// <para>
/// The fixture is an empty database. Every admin page falls through to its no-data branch,
/// which still renders the header, the nav, the table headings and the empty state — and the
/// section's copy all lives on the transfer wizard, which renders its whole set with no
/// tickets held. Seeding orders would exercise <c>TicketQueryService</c>, which is
/// <c>Humans.Tickets.Tests</c>' business, and would have to go through the service rather than
/// the context anyway: <c>CachingTicketQueryService</c> is a Singleton that warms at startup,
/// before the test body (Calendar's finding).
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
        ("/Tickets/GateList", "Gate List"),
        ("/Tickets/Participation/Backfill", "Backfill Participation Data"),
        ("/Tickets/Admin/Transfers", "Ticket transfer requests"),
        ("/Tickets/Admin/Onsite", "Who's Onsite"),
        // /Tickets/Admin/Contacts is deliberately absent: AttendeeContactImportService.BuildPlanAsync
        // throws "No active vendor event id — sync has not run" against an empty database, which
        // predates this move and is the page's designed behaviour. Its view is covered by
        // Humans.Tickets.Tests/Controllers/TicketsContactsAdminControllerTests instead.
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

            // A view component element the section's _ViewImports failed to bind renders as
            // literal <vc:…> markup: 200, correct-looking source, nothing on the page.
            html.Should().NotContain("<vc:", $"GET {url} left a view-component tag unrendered");

            // ReSharper rewrites <vc:name> to <name-view-component> when it mistakes the
            // element for a type reference; that also renders as inert markup.
            html.Should().NotContain("-view-component", $"GET {url} has a rewritten vc tag");

            // The fallback for a key the carve missed is the key itself.
            html.Should().NotContain("TicketTransfer_", $"GET {url} rendered a raw resource key");
        }
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_dashboard_renders_the_access_matrix_tag_helper()
    {
        // AccessMatrixViewComponent moved into Humans.UI (nobodies-collective/Humans#1056),
        // so the call site is <vc:access-matrix section="Tickets" /> bound through
        // @addTagHelper *, Humans.UI. An unbound <vc:> ships as inert literal markup with a
        // green build and no log line, so the emitted modal id is the proof — not the 200.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var html = await (await Client.GetAsync("/Tickets", ct)).Content.ReadAsStringAsync(ct);

        html.Should().Contain("sectionHelp-Tickets", "the access-matrix widget must bind and render from a section view");
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_transfer_wizard_renders_the_sections_own_resource_set_and_ticket_stub()
    {
        // Every Localizer[…] call in the section is on this page, so it is the whole resx
        // half of step 12. It is also the only place <vc:ticket-stub> is reached from a
        // section view — the component moved out of Humans.UI into Humans.Tickets/Contracts/,
        // public so MVC's default provider discovers it and the tag helper is generated.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);

        var response = await Client.GetAsync("/Tickets/Transfers", ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await response.Content.ReadAsStringAsync(ct);
        // The signed-in persona holds no tickets, so the page takes its no-tickets branch —
        // which still renders three of the carved keys plus the shared Common_Back. The
        // apostrophe in TicketTransfer_NoTickets is typographic and Razor's HtmlEncoder turns
        // it into a numeric entity, so the assertion stays on an ASCII-only run.
        html.Should().Contain("Transfer a ticket");                    // TicketTransfer_Title
        html.Should().Contain("have any tickets to transfer");         // TicketTransfer_NoTickets
        html.Should().Contain("Your transfer requests");               // TicketTransfer_YourRequests
        html.Should().Contain("Back");                                 // Common_Back, from SharedResource
        html.Should().NotContain("TicketTransfer_", "a carved key rendered as its own name");
        html.Should().NotContain("Common_", "a shared key rendered as its own name");
        html.Should().NotContain("<vc:");
    }

    [HumansFact(Timeout = 120000)]
    public async Task A_volunteer_cannot_reach_the_ticket_admin_pages()
    {
        // The section's negative access rule. Worth pinning at the move because the
        // controllers are now internal types in another assembly, routed through
        // SectionControllerFeatureProvider, while TicketAdminBoardOrAdmin stayed in Shell's
        // AuthorizationPolicyExtensions (design §8's asymmetry). The shape is a 302 to
        // Program.cs's AccessDeniedPath, not a bare 403 — cookie authentication redirects an
        // authenticated-but-unauthorized request, app-wide (Cantina's finding).
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
        // An English-only check passes whether or not the RCL's satellites shipped — the
        // neutral set is embedded in the main assembly and the fallback is silent.
        // Razor's default HtmlEncoder escapes non-ASCII to numeric entities, so the
        // assertions stay on ASCII-only runs of the Spanish copy.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);

        // Accept-Language does not reach a signed-in user: Program.cs's initial culture
        // provider returns the user's PreferredLanguage and short-circuits the rest of the
        // chain, and the wizard is [Authorize]. Switch the language the way the UI does.
        var switcherPage = await (await Client.GetAsync("/Tickets/Transfers", ct)).Content.ReadAsStringAsync(ct);
        var token = ExtractAntiForgeryToken(switcherPage);
        token.Should().NotBeNullOrEmpty();
        await Client.PostAsync("/Language/SetLanguage", new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("__RequestVerificationToken", token!),
                new KeyValuePair<string, string>("culture", "es"),
            ]), ct);

        var response = await Client.GetAsync("/Tickets/Transfers", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync(ct);

        html.Should().Contain("Transferir una entrada");         // TicketTransfer_Title
        html.Should().Contain("Tus solicitudes de transferencia"); // TicketTransfer_YourRequests
        html.Should().NotContain("Transfer a ticket");
    }

    [HumansFact(Timeout = 120000)]
    public async Task Shell_renders_the_sections_ticket_stub_component()
    {
        // The section-view call site is covered above; this is the *Shell* half, and it is a
        // separate assertion because it fails separately. TicketStubViewComponent moved out of
        // Humans.UI into Humans.Tickets/Contracts/, and an @using of the namespace does not
        // import the tag helper — Shell needs its own `@addTagHelper *, Humans.Tickets` in
        // Views/_ViewImports.cshtml. Without it the element ships as inert literal markup:
        // 200, correct-looking source, and the ticket cards simply absent from the homepage
        // dashboard, the holdings list and this page.
        //
        // /WidgetGallery is the probe because it renders three stub variants from inline
        // sample data — no seeded orders, and it is the only page that exercises the void and
        // early-entry branches together.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var response = await Client.GetAsync("/WidgetGallery", ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await response.Content.ReadAsStringAsync(ct);
        html.Should().NotContain("<vc:ticket-stub", "Shell must import the Tickets tag helper, not just the namespace");
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
