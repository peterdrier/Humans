using System.Net;
using AwesomeAssertions;
using Humans.Integration.Tests.Infrastructure;
using Xunit;

namespace Humans.Integration.Tests.Controllers;

public class CalendarControllerTests : IntegrationTestBase
{
    public CalendarControllerTests(HumansWebApplicationFactory factory) : base(factory) { }

    [HumansFact]
    public async Task Anonymous_GET_Calendar_redirects_to_login()
    {
        var resp = await Client.GetAsync("/Calendar");

        // Cookie auth redirects unauthenticated requests to a login challenge.
        resp.StatusCode.Should().BeOneOf(HttpStatusCode.Redirect, HttpStatusCode.Found, HttpStatusCode.Unauthorized);
    }

    [HumansFact]
    public async Task LoggedIn_Volunteer_can_GET_Calendar()
    {
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);

        var resp = await Client.GetAsync("/Calendar");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [HumansFact]
    public async Task LoggedIn_Volunteer_can_GET_Create()
    {
        // Calendar editing is open to any authenticated human; changes are audited.
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);

        var resp = await Client.GetAsync("/Calendar/Event/Create");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [HumansFact]
    public async Task LoggedIn_Admin_GET_Agenda_renders()
    {
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var resp = await Client.GetAsync("/Calendar/Agenda");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
