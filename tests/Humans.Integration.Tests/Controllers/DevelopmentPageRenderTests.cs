using System.Net;
using AwesomeAssertions;
using Humans.Integration.Tests.Infrastructure;

namespace Humans.Integration.Tests.Controllers;

/// <summary>Renders the Development section through the real app.</summary>
/// <remarks>
/// <para>
/// No resource set or <c>wwwroot/</c>, so no raw-key, Spanish or asset checks. The login page renders the section's
/// <c>_DevLoginPanel</c> by name (a miss throws), and the persona buttons are checked because an empty list renders an
/// empty panel.
/// </para>
/// <para>
/// Signing in proves <c>Section.Register</c>'s environment gate reads the host's environment: it fails closed, so a
/// broken key unregisters <c>DevPersonaSeeder</c> and every dev sign-in in the suite dies, rather than the seeders
/// reaching Production. The Production exclusion of the controller can't run under "Testing"; it throws on a name
/// miss instead.
/// </para>
/// </remarks>
public class DevelopmentPageRenderTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    [HumansFact(Timeout = 120000)]
    public async Task The_login_page_renders_the_sections_dev_panel_partial()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;

        var response = await Client.GetAsync("/Account/Login", ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await response.Content.ReadAsStringAsync(ct);
        html.Should().Contain("Dev login",
            because: "_DevLoginPanel.cshtml lives in Humans.Development and Shell finds it by name");
        html.Should().Contain("/dev/login/volunteer",
            because: "the panel enumerates the section-internal persona list");
        html.Should().Contain("/dev/login/users",
            because: "the panel's user-chooser link must still resolve the section's route");
        AssertTagHelpersRendered(html, "/Account/Login");
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_user_chooser_renders_its_table_through_the_shared_partial()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var response = await Client.GetAsync("/dev/login/users", ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await response.Content.ReadAsStringAsync(ct);
        html.Should().Contain("Dev Login — Sign in as any user");
        // _Table resolves by name across parts; the built TableModel puts the seeded persona in a row.
        html.Should().Contain("<table");
        html.Should().Contain("dev-admin@localhost");
        AssertTagHelpersRendered(html, "/dev/login/users");
    }

    [HumansFact(Timeout = 120000)]
    public async Task Dev_login_seeds_a_persona_and_redirects_home()
    {
        // The controller takes DevPersonaSeeder, so an unregistered seeder 500s here.
        var ct = Xunit.TestContext.Current.CancellationToken;

        var response = await Client.GetAsync("/dev/login/volunteer", ct);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        var location = response.Headers.Location;
        location.Should().NotBeNull();
        location.OriginalString.Should().StartWith("/",
            because: "the no-returnUrl branch redirects to Home/Index by string literal — "
                   + "HomeController is Shell's and the section cannot name it");

        var landing = await Client.GetAsync(location, ct);
        landing.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Found);
    }

    [HumansFact(Timeout = 120000)]
    public async Task A_volunteer_cannot_reach_the_admin_gated_seed_endpoints()
    {
        // Cookie auth redirects an authenticated-but-unauthorized request to AccessDeniedPath: a 302, not a 403.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);

        foreach (var url in new[] { "/dev/seed/budget", "/dev/seed/camp-roles", "/dev/seed/dashboard/reset" })
        {
            var response = await Client.PostAsync(url, new StringContent(string.Empty), ct);
            response.StatusCode.Should().Be(HttpStatusCode.Found, $"POST {url} must be gated");
            response.Headers.Location?.AbsolutePath.Should().Be("/Account/AccessDenied",
                because: $"POST {url} must be denied, not sent to sign in");
        }
    }

    private static void AssertTagHelpersRendered(string html, string url)
    {
        // An unbound tag helper survives as its own start tag: a 200 with a missing widget.
        html.Should().NotContain("<page-header", $"GET {url} left a tag helper unrendered");
        // ReSharper can rewrite <vc:name> to <name-view-component>, which also renders inert.
        html.Should().NotContain("-view-component", $"GET {url} has a rewritten vc tag");
    }
}
