using System.Net;
using AwesomeAssertions;
using Humans.Integration.Tests.Infrastructure;

namespace Humans.Integration.Tests.Controllers;

/// <summary>
/// Renders the Development section through the real app, as the standing form of the §15
/// step 12 check for its move into <c>src/Sections/Humans.Development</c>
/// (nobodies-collective/Humans#866, G5).
/// </summary>
/// <remarks>
/// <para>
/// Development has none of the usual halves — no resource set (every string is English
/// developer copy, so no <c>NotContain("Development_")</c> and no Spanish request) and no
/// <c>wwwroot/</c>, so no <c>?v=</c> hash. What is left is what this particular move breaks,
/// and every one of these failures returns 200:
/// </para>
/// <list type="number">
/// <item><description>
/// Shell's <c>/Account/Login</c> used to enumerate <c>DevLoginController.AllPersonas</c>
/// directly. The persona list is <c>internal</c> now and the markup moved into the section's
/// <c>Views/Shared/_DevLoginPanel.cshtml</c>, which Shell pulls in by name across application
/// parts. A partial that fails to resolve throws, so the anonymous GET of the login page is the
/// whole assertion — but the buttons are checked too, because an empty persona list would
/// render an empty panel.
/// </description></item>
/// <item><description>
/// <c>/dev/login/users</c> is the section's own view, and its table comes from
/// <c>Humans.UI</c>'s <c>_Table</c> partial. A section RCL does not inherit the host's
/// <c>Views/_ViewImports.cshtml</c>.
/// </description></item>
/// <item><description>
/// Signing in is the section's actual job, and it is the load-bearing proof that
/// <c>Section.Register</c>'s environment gate reads the host's environment: the seeders are
/// registered only outside Production, off <c>HostDefaults.EnvironmentKey</c> in the
/// configuration Shell passes to <c>ISection.Register</c>. If that key stopped resolving, the
/// gate fails closed, <c>DevPersonaSeeder</c> is unregistered and every dev sign-in in the
/// whole suite dies here rather than three dev seeders quietly reaching Production.
/// </description></item>
/// </list>
/// <para>
/// The Production half — <c>DevLoginControllerExclusionProvider</c> removing the now-internal
/// controller from MVC's feature — cannot be exercised from a test host running as "Testing".
/// It resolves the controller by name through <c>SectionDiscoveryExtensions</c> and throws on a
/// miss, which is the guard that a rename cannot pass silently.
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
        // <partial name="_Table"> lives in Humans.UI and resolves by name across application
        // parts; the built TableModel is what puts the seeded persona in a row.
        html.Should().Contain("<table");
        html.Should().Contain("dev-admin@localhost");
        AssertTagHelpersRendered(html, "/dev/login/users");
    }

    [HumansFact(Timeout = 120000)]
    public async Task Dev_login_seeds_a_persona_and_redirects_home()
    {
        // Proves the section's Section.Register ran and bound DevPersonaSeeder: the controller
        // takes it as a constructor dependency, so an unbound gate 500s here.
        var ct = Xunit.TestContext.Current.CancellationToken;

        var response = await Client.GetAsync("/dev/login/volunteer", ct);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        var location = response.Headers.Location;
        location.Should().NotBeNull();
        location!.OriginalString.Should().StartWith("/",
            because: "the no-returnUrl branch redirects to Home/Index by string literal — "
                   + "HomeController is Shell's and the section cannot name it");

        var landing = await Client.GetAsync(location, ct);
        landing.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Found);
    }

    [HumansFact(Timeout = 120000)]
    public async Task A_volunteer_cannot_reach_the_admin_gated_seed_endpoints()
    {
        // The section's negative access rule. Worth pinning at the move because both
        // controllers are now internal types in another assembly routed through
        // SectionControllerFeatureProvider, while their policies stayed in Shell's
        // AuthorizationPolicyExtensions (design §8's asymmetry).
        //
        // 302 to Program.cs's AccessDeniedPath, not 403: cookie authentication redirects an
        // authenticated-but-unauthorized request, app-wide (Cantina's rule).
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
        // An unbound Humans.UI tag helper neither throws nor degrades — it survives into the
        // response as its own start tag, so the page is a 200 with correct-looking source and a
        // missing widget.
        html.Should().NotContain("<page-header", $"GET {url} left a tag helper unrendered");
        // ReSharper rewrites <vc:name> to <name-view-component> when it mistakes the element
        // for a type reference; that also renders as inert markup.
        html.Should().NotContain("-view-component", $"GET {url} has a rewritten vc tag");
    }
}
