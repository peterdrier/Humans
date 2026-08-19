using System.Net;
using AwesomeAssertions;
using Humans.Integration.Tests.Infrastructure;

namespace Humans.Integration.Tests.Controllers;

/// <summary>
/// Renders <c>/Finance/Holded</c> — the connector index added by
/// nobodies-collective/Humans#1000 — through the real app.
/// </summary>
/// <remarks>
/// The page is pure link-out plus three cached tables, so its failure modes are the silent ones
/// a green build does not catch. A section RCL does not inherit the host's
/// <c>Views/_ViewImports.cshtml</c>, and an anchor tag helper naming a controller/action pair
/// that resolves to no endpoint omits its <c>href</c> entirely while still returning 200 with a
/// link-shaped element that goes nowhere (the A2 incident behind
/// <see cref="AdminNavTreeRoutingTests"/>). This page's whole purpose is those links, so each one
/// is asserted as a real href rather than as "the page loaded".
/// <para>
/// Nothing is seeded: the empty database is the state the staleness alarm has to be legible in —
/// a connector that has never synced is exactly as wrong as a stalled one, and rendering that
/// blank was the gap the issue names.
/// </para>
/// </remarks>
public class FinancePageRenderTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    [HumansFact(Timeout = 120000)]
    public async Task The_connector_index_renders_and_links_out_to_every_screen_it_defers_to()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var response = await Client.GetAsync("/Finance/Holded", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "GET /Finance/Holded must render");
        var html = await response.Content.ReadAsStringAsync(ct);

        html.Should().Contain("Holded Connector");

        // "Link out, don't rebuild" is the issue's constraint; an href-less anchor silently
        // breaks it while the page still looks right.
        foreach (var href in new[]
                 {
                     "/Finance/HoldedAccounts",
                     "/Finance/HoldedUnmatched",
                     "/Finance/Creditors",
                     "/Holded",
                 })
        {
            html.Should().Contain($"href=\"{href}\"", $"the index must link to {href}");
        }

        // A view-component element the section's _ViewImports failed to bind renders as literal
        // markup: 200, correct-looking source, nothing on the page.
        html.Should().NotContain("<vc:", "a view-component tag was left unrendered");
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_connector_index_offers_no_write_action()
    {
        // Read-only is the issue's constraint, not an accident of what got built: every trigger —
        // Sync now, Provision, Bind, Unbind — stays on the screen that already owns it. A stray
        // POST form here would be the first step back toward a second place to mutate from.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var html = await (await Client.GetAsync("/Finance/Holded", ct)).Content.ReadAsStringAsync(ct);

        // Named routes, not "no <form>": the admin layout's own chrome (language switcher,
        // sign-out) posts, so the assertion has to be about this section's write endpoints.
        foreach (var writeRoute in new[]
                 {
                     "/Finance/HoldedSync/Run",
                     "/Finance/HoldedAccounts/Provision",
                     "/Finance/Creditors/Bind",
                     "/Finance/Creditors/Unbind",
                 })
        {
            html.Should().NotContain(writeRoute, $"the connector index is read-only, but posts to {writeRoute}");
        }
    }

    [HumansFact(Timeout = 120000)]
    public async Task A_connector_that_has_never_synced_says_so_rather_than_rendering_blank()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var html = await (await Client.GetAsync("/Finance/Holded", ct)).Content.ReadAsStringAsync(ct);

        html.Should().Contain("never completed", "staleness must be legible, not implied");
        html.Should().Contain("Stale");
    }
}
