using System.Net;
using AwesomeAssertions;
using Humans.Integration.Tests.Infrastructure;
using Xunit;

namespace Humans.Integration.Tests.Controllers;

public class CalendarControllerTests : IntegrationTestBase
{
    public CalendarControllerTests(HumansWebApplicationFactory factory) : base(factory) { }

    [Fact]
    public async Task Anonymous_GET_Calendar_redirects_to_login()
    {
        var resp = await Client.GetAsync("/Calendar");

        // Cookie auth redirects unauthenticated requests to a login challenge.
        resp.StatusCode.Should().BeOneOf(HttpStatusCode.Redirect, HttpStatusCode.Found, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task LoggedIn_Volunteer_can_GET_Calendar()
    {
        // 1) Sign in as a bare volunteer via dev-login (sets auth cookie).
        var loginResp = await Client.GetAsync("/dev/login/volunteer");
        loginResp.StatusCode.Should().BeOneOf(HttpStatusCode.Redirect, HttpStatusCode.Found, HttpStatusCode.OK);

        // 2) Hit the month view — should render 200.
        var resp = await Client.GetAsync("/Calendar");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task LoggedIn_Volunteer_can_GET_Create()
    {
        // Calendar editing is open to any authenticated human; changes are audited.
        await Client.GetAsync("/dev/login/volunteer");

        var resp = await Client.GetAsync("/Calendar/Event/Create");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task LoggedIn_Admin_GET_Agenda_renders()
    {
        await Client.GetAsync("/dev/login/admin");

        var resp = await Client.GetAsync("/Calendar/Agenda");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
