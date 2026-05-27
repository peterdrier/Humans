using System.Net;
using AwesomeAssertions;
using Humans.Integration.Tests.Infrastructure;

namespace Humans.Integration.Tests.Controllers;

public class StoreSummaryControllerTests(HumansWebApplicationFactory factory)
    : IntegrationTestBase(factory)
{
    [HumansFact(Timeout = 60000)]
    public async Task Volunteer_GET_admin_summary_returns_403_or_redirect()
    {
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);

        var resp = await Client.GetAsync("/Store/Admin/Summary");

        ((int)resp.StatusCode).Should().BeOneOf(
            (int)HttpStatusCode.Forbidden,
            (int)HttpStatusCode.Found,
            (int)HttpStatusCode.Redirect);
    }

    [HumansFact(Timeout = 60000)]
    public async Task StoreAdmin_GET_admin_summary_returns_200_with_three_sections()
    {
        await Factory.SignInAsFullyOnboardedAsync(Client, new DevPersona("store-admin"));

        var resp = await Client.GetAsync("/Store/Admin/Summary");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("By counterparty");
        body.Should().Contain("By item");
        body.Should().Contain("Counterparties × products");
    }
}
