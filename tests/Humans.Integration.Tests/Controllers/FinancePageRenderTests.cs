using System.Net;
using AwesomeAssertions;
using Humans.Integration.Tests.Infrastructure;

namespace Humans.Integration.Tests.Controllers;

/// <summary>Renders <c>/Finance</c> and <c>/Holded/Purchases</c> through the real app.</summary>
/// <remarks>
/// An anchor tag helper whose controller/action resolves to no endpoint omits its <c>href</c> and still returns 200,
/// and both pages link out, so each link is asserted as a real href.
/// </remarks>
public class FinancePageRenderTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    [HumansFact(Timeout = 120000)]
    public async Task The_finance_index_renders_and_links_to_every_finance_screen()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var response = await Client.GetAsync("/Finance", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "GET /Finance must render");
        var html = await response.Content.ReadAsStringAsync(ct);
        foreach (var href in new[]
                 {
                     "/Finance/CashFlow",
                     "/Finance/Creditors",
                     "/Finance/Sepa",
                     "/Finance/ExpenseRecon",
                     "/Finance/HoldedAccounts",
                     "/Finance/HoldedUnmatched",
                 })
        {
            html.Should().Contain($"href=\"{href}\"", $"the index must link to {href}");
        }
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_purchase_mirror_renders_unsynced_and_links_to_finance_attribution()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var response = await Client.GetAsync("/Holded/Purchases", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "GET /Holded/Purchases must render");
        var html = await response.Content.ReadAsStringAsync(ct);
        html.Should().Contain("No documents mirrored yet");
        html.Should().Contain("href=\"/Finance/HoldedUnmatched\"");
    }
}
