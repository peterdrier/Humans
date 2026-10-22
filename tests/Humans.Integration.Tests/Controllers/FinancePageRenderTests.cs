using System.Net;
using AwesomeAssertions;
using Humans.Integration.Tests.Infrastructure;

namespace Humans.Integration.Tests.Controllers;

/// <summary>Renders <c>/Finance/Holded</c> through the real app.</summary>
/// <remarks>
/// An anchor tag helper whose controller/action resolves to no endpoint omits its <c>href</c> and still returns 200,
/// and this page is all links, so each is asserted as a real href.
/// <para>
/// Nothing is seeded: a connector that has never synced is as wrong as a stalled one, and the staleness alarm must be
/// legible on an empty database.
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

        // The page links out rather than rebuilding; an href-less anchor breaks that while looking right.
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
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_connector_index_offers_no_write_action()
    {
        // Read-only by design: every trigger stays on the screen that owns it, so there's one place to mutate from.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var html = await (await Client.GetAsync("/Finance/Holded", ct)).Content.ReadAsStringAsync(ct);

        // Named routes, not "no <form>": the admin chrome's language switcher and sign-out post.
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
