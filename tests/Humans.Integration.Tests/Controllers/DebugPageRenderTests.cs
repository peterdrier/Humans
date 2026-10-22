using System.Net;
using AwesomeAssertions;
using Humans.Integration.Tests.Infrastructure;

namespace Humans.Integration.Tests.Controllers;

/// <summary>Renders every Debug page through the real app.</summary>
/// <remarks>
/// Debug has no resource set or <c>wwwroot/</c>, so no raw-key or Spanish checks. What breaks here still returns 200:
/// unbound <c>&lt;page-header&gt;</c> and <c>&lt;vc:human&gt;</c> tags, gallery models that no longer bind, and the
/// anonymous <c>/Debug/DbVersion</c> the deployment tooling polls. The telemetry shown is in-memory, so each page
/// renders headings, cards and its empty state.
/// </remarks>
public class DebugPageRenderTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    private static (string Url, string Copy)[] Pages =>
    [
        ("/Debug/Logs", "Application Logs"),
        ("/Debug/HttpErrors", "HTTP Errors"),
        ("/Debug/Maintenance", "Maintenance"),
        ("/Debug/DataMigrations", "Data migrations"),
        ("/Debug/Configuration", "Configuration Status"),
        ("/Debug/DbStats", "Database Query Statistics"),
        ("/Debug/CacheStats", "Cache Statistics"),
        ("/Debug/ClientStats", "Client Statistics"),
        ("/Debug/Timings", "Operation Timings"),
        ("/Debug/Sections", "Sections"),
        ("/Debug/FormatGallery", "Date format gallery"),
        ("/Debug/Translations", "Translations gallery"),
    ];

    [HumansFact(Timeout = 120000)]
    public async Task Every_debug_page_renders_its_own_copy_with_tag_helpers_applied()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        foreach (var (url, copy) in Pages)
        {
            var response = await Client.GetAsync(url, ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {url} must render");

            var html = await response.Content.ReadAsStringAsync(ct);
            html.Should().Contain(copy, $"GET {url} must render its own copy");

            // An unbound tag helper survives as its own element name: 200, nothing on screen. The positive marker is
            // asserted on /Debug/Translations below.
            html.Should().NotContain("<page-header", $"GET {url} left a tag helper unrendered");

            // ReSharper can rewrite <vc:name> to <name-view-component>, which also renders inert.
            html.Should().NotContain("-view-component", $"GET {url} has a rewritten vc tag");
        }
    }

    /// <summary>
    /// <c>/Debug/Maintenance</c>'s only control posts to a real action; the sweep's heading match passes on a page that
    /// lost its form. AdminOnly, and E2E has no Admin persona, so it lives here.
    /// </summary>
    [HumansFact(Timeout = 120000)]
    public async Task The_maintenance_page_renders_its_clear_hangfire_locks_form()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var html = await (await Client.GetAsync("/Debug/Maintenance", ct)).Content.ReadAsStringAsync(ct);

        html.Should().Contain("ClearHangfireLocks",
            "asp-action must resolve to an href — a pair that routes nowhere renders a form "
            + "that posts to the current page instead");
        html.Should().Contain("Clear Hangfire Locks", "the page's only control must render");
        html.Should().Contain("__RequestVerificationToken",
            "the form posts, so it must carry its antiforgery token");
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_two_reflection_galleries_render_their_rows_from_both_sides_of_the_move()
    {
        // Both galleries are reflection-built, so a broken binding renders an empty table; a 200 passes either way.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var formats = await (await Client.GetAsync("/Debug/FormatGallery", ct)).Content.ReadAsStringAsync(ct);
        formats.Should().Contain("IcalBasicDatePattern",
            because: "the gallery reflects over DateFormattingExtensions' pattern fields");
        formats.Should().Contain("Europe/Madrid");

        var translations = await (await Client.GetAsync("/Debug/Translations", ct)).Content.ReadAsStringAsync(ct);
        // The positive half of the <page-header> check: the h1 the tag helper emits.
        translations.Should().Contain("<h1 class=\"mb-0\">",
            because: "<page-header> must render its h1, not merely be absent from the body");
        translations.Should().Contain(" keys",
            because: "the gallery reports the SharedResource key count it enumerated");
        translations.Should().Contain("Common_",
            because: "the page lists shared resource keys as data — the one place a raw key "
                   + "prefix in the body is correct rather than a missed carve");
    }

    [HumansFact(Timeout = 120000)]
    public async Task DbVersion_stays_anonymous_and_returns_migration_json()
    {
        // Deployment tooling polls this unauthenticated: [AllowAnonymous] inside an [Authorize] controller.
        var ct = Xunit.TestContext.Current.CancellationToken;

        var response = await Client.GetAsync("/Debug/DbVersion", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        var json = await response.Content.ReadAsStringAsync(ct);
        json.Should().Contain("appliedCount").And.Contain("sections");
    }

    [HumansFact(Timeout = 120000)]
    public async Task A_volunteer_cannot_reach_any_admin_gated_debug_page()
    {
        // Cookie auth redirects an authenticated-but-unauthorized request to AccessDeniedPath: a 302, not a 403.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Volunteer);

        foreach (var url in new[] { "/Debug/Logs", "/Debug/Configuration", "/Debug/CacheStats", "/Debug/Maintenance" })
        {
            var response = await Client.GetAsync(url, ct);
            response.StatusCode.Should().Be(HttpStatusCode.Found, $"GET {url} must be gated");
            response.Headers.Location?.AbsolutePath.Should().Be("/Account/AccessDenied",
                because: $"GET {url} must be denied, not sent to sign in");
        }

        // …and the anonymous exception is still reachable for the same persona.
        var dbVersion = await Client.GetAsync("/Debug/DbVersion", ct);
        dbVersion.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
