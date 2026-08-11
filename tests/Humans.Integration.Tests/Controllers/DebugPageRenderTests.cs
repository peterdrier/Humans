using System.Net;
using AwesomeAssertions;
using Humans.Integration.Tests.Infrastructure;

namespace Humans.Integration.Tests.Controllers;

/// <summary>
/// Renders every Debug page through the real app, as the standing form of the §15 step 12
/// check for the section's move into <c>src/Sections/Humans.Debug</c>
/// (nobodies-collective/Humans#866, G5).
/// </summary>
/// <remarks>
/// <para>
/// Debug has none of the usual halves — no resource set (every string is English developer
/// copy), no <c>wwwroot/</c>, therefore no <c>NotContain("Debug_")</c> and no Spanish request.
/// What is left is what a G5 move actually breaks here, and all of it returns 200:
/// </para>
/// <list type="number">
/// <item><description>
/// A section RCL does not inherit the host's <c>Views/_ViewImports.cshtml</c>. Every page
/// opens with <c>&lt;page-header&gt;</c> and two of them bind <c>&lt;vc:human&gt;</c> — an
/// unbound tag helper survives into the response as inert literal markup with no error, so
/// the assertions are on the *rendered* form, never on the page loading.
/// </description></item>
/// <item><description>
/// The view models moved out of <c>Humans.Web/Models</c> and turned internal, while
/// <c>TranslationsGalleryModelBuilder</c> went the other way, down to <c>Humans.UI</c>. One
/// GET of each gallery is what proves both halves of that split still bind.
/// </description></item>
/// <item><description>
/// <c>/Debug/DbVersion</c> is <c>[AllowAnonymous]</c> inside an <c>[Authorize]</c> controller
/// and is what the deployment tooling polls. Its route now resolves through
/// <c>SectionControllerFeatureProvider</c> against an internal controller, so the anonymous
/// GET is worth pinning at the move.
/// </description></item>
/// </list>
/// <para>
/// The fixture is an empty database and a freshly started process: the telemetry the pages
/// display is in-memory and accumulates from the test host's own requests, so each page
/// renders its headings, its cards and its empty state — which is all that any of these
/// assertions is about.
/// </para>
/// </remarks>
public class DebugPageRenderTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    private static (string Url, string Copy)[] Pages =>
    [
        ("/Debug/Logs", "Application Logs"),
        ("/Debug/HttpErrors", "HTTP Errors"),
        ("/Debug/Maintenance", "Maintenance"),
        ("/Debug/Configuration", "Configuration Status"),
        ("/Debug/DbStats", "Database Query Statistics"),
        ("/Debug/CacheStats", "Cache Statistics"),
        ("/Debug/ClientStats", "Client Statistics"),
        ("/Debug/Timings", "Operation Timings"),
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

            // <page-header> is Humans.UI's tag helper. A section can only addTagHelper against
            // Humans.UI, and a helper the section's _ViewImports failed to bind survives into
            // the body as its own element name — 200, correct-looking source, nothing on screen.
            html.Should().NotContain("<page-header", $"GET {url} left a tag helper unrendered");

            // Same failure mode for a view component element.
            html.Should().NotContain("<vc:", $"GET {url} left a view-component tag unrendered");

            // ReSharper rewrites <vc:name> to <name-view-component> when it mistakes the
            // element for a type reference; that also renders as inert markup.
            html.Should().NotContain("-view-component", $"GET {url} has a rewritten vc tag");
        }
    }

    [HumansFact(Timeout = 120000)]
    public async Task The_two_reflection_galleries_render_their_rows_from_both_sides_of_the_move()
    {
        // FormatGalleryModelBuilder moved into the section and turned internal;
        // TranslationsGalleryModelBuilder went down to Humans.UI instead, because it enumerates
        // SharedResource and a Base test asserts translation parity through it. Both pages are
        // reflection-built, so an empty table is the shape a broken binding takes — "the page
        // renders" would pass over either of them.
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);

        var formats = await (await Client.GetAsync("/Debug/FormatGallery", ct)).Content.ReadAsStringAsync(ct);
        formats.Should().Contain("IcalBasicDatePattern",
            because: "the gallery reflects over DateFormattingExtensions' pattern fields");
        formats.Should().Contain("Europe/Madrid");

        var translations = await (await Client.GetAsync("/Debug/Translations", ct)).Content.ReadAsStringAsync(ct);
        translations.Should().Contain(" keys",
            because: "the gallery reports the SharedResource key count it enumerated");
        translations.Should().Contain("Common_",
            because: "the page lists shared resource keys as data — the one place a raw key "
                   + "prefix in the body is correct rather than a missed carve");
    }

    [HumansFact(Timeout = 120000)]
    public async Task DbVersion_stays_anonymous_and_returns_migration_json()
    {
        // The one action deliberately outside the section's AdminOnly gate: deployment tooling
        // polls it unauthenticated. [AllowAnonymous] on an action of an [Authorize] controller
        // now routed out of another assembly is exactly the pairing a move can break.
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
        // The section's negative access rule. Worth pinning at the move because the controller
        // is now an internal type in another assembly, routed through
        // SectionControllerFeatureProvider, while AdminOnly stayed in Shell's
        // AuthorizationPolicyExtensions (design §8's asymmetry).
        //
        // The shape is a 302 to Program.cs's AccessDeniedPath, not a bare 403: cookie
        // authentication redirects an authenticated-but-unauthorized request, app-wide.
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
