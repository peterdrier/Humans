using System.Net;
using AwesomeAssertions;
using Humans.Guide.Services;
using Humans.Integration.Tests.Infrastructure;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Integration.Tests.Controllers;

/// <summary>
/// Renders every Guide page through the real app, as the standing form of the §15 step 12
/// check for the section's move into <c>src/Sections/Humans.Guide</c>
/// (nobodies-collective/Humans#866, G5).
/// </summary>
/// <remarks>
/// <para>
/// Guide has no resource set and no static assets, so the usual resx and <c>?v=</c> halves of
/// this suite do not apply. What is left is the part that fails as a <b>200 with degraded
/// content</b>:
/// </para>
/// <list type="number">
/// <item><description>
/// A section RCL does not inherit the host's <c>Views/_ViewImports.cshtml</c>. Guide's one
/// tag-helper dependency is <c>Humans.UI</c>'s <c>AuthorizeViewTagHelper</c> on the refresh
/// form; without <c>@@addTagHelper *, Humans.Interfaces</c> the build stays green, the attribute
/// survives into the response as literal <c>authorize-policy="AdminOnly"</c>, and the form is
/// rendered <em>to everyone</em>. Asserting the attribute is absent is the only thing that
/// catches it.
/// </description></item>
/// <item><description>
/// <c>_GuideLayout</c> is a partial that moved out of <c>Humans.Web/Views/Shared</c> into the
/// section's own <c>Views/Shared</c>. Partial lookup resolves by name across application
/// parts, but a miss throws only for <c>PartialAsync</c> — asserting a sidebar entry is what
/// proves it is the section's copy being found.
/// </description></item>
/// </list>
/// <para>
/// The fixture writes rendered HTML straight into the shared <c>IMemoryCache</c> under the
/// section's <c>guide:&lt;stem&gt;</c> key. <c>GuideContentService.GetRenderedAsync</c> returns
/// a cache hit without touching GitHub, so the pages under test never make a network call —
/// otherwise every run would fetch 28 files from <c>nobodies-collective/Humans@main</c>
/// anonymously and 503 the moment the rate limit or the network said no.
/// </para>
/// </remarks>
public class GuidePageRenderTests(HumansTestDatabase database) : IntegrationTestBase(database)
{
    private const string Markdown = """
        # Fixture page

        Intro copy, always visible.

        ## As a Volunteer

        Volunteer-scoped copy.

        ## As a Board member / Admin (Teams Admin)

        Board-scoped copy.

        ## Related sections

        Trailer copy, always visible.
        """;

    private void SeedRenderedPages(params string[] stems)
    {
        var renderer = Factory.Services.GetRequiredService<IGuideRenderer>();
        var cache = Factory.Services.GetRequiredService<IMemoryCache>();

        foreach (var stem in stems)
        {
            // Mirrors GuideContentService.CacheKey — a private const in the section, which the
            // test project can see the type of but not the value.
            cache.Set($"guide:{stem}", renderer.Render(Markdown, stem));
        }
    }

    [HumansFact(Timeout = 120000)]
    public async Task Every_guide_page_renders_its_sidebar_and_applies_the_shared_tag_helpers()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);
        SeedRenderedPages("README", "Profiles", "Glossary");

        foreach (var url in new[] { "/Guide", "/Guide/Profiles", "/Guide/Glossary" })
        {
            var response = await Client.GetAsync(url, ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {url} must render");

            var html = await response.Content.ReadAsStringAsync(ct);

            html.Should().Contain("Intro copy, always visible.",
                $"GET {url} must render the page body through the section's own view");
            html.Should().Contain("Getting Started",
                $"GET {url} must render _GuideLayout's sidebar from the section's Views/Shared");
            html.Should().Contain("Common questions",
                $"GET {url} must render the sidebar's Common questions group");

            // A view-component element the section's _ViewImports failed to bind renders as
            // literal <vc:…> markup: 200, correct-looking source, nothing on the page.
            html.Should().NotContain("<vc:", $"GET {url} left a view-component tag unrendered");

            // An unbound AuthorizeViewTagHelper leaks its attribute into the response instead
            // of gating the element.
            html.Should().NotContain("authorize-policy",
                $"GET {url} left Humans.UI's authorize-policy tag helper unbound");
        }
    }

    [HumansFact(Timeout = 120000)]
    public async Task Role_scoped_blocks_are_filtered_per_viewer()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;

        // Anonymous: /Guide is [AllowAnonymous] and sees Volunteer blocks only. This also
        // proves the section's controller routes at all without a signed-in principal.
        SeedRenderedPages("README");
        var anonymous = await Client.GetAsync("/Guide", ct);
        anonymous.StatusCode.Should().Be(HttpStatusCode.OK);
        var anonymousHtml = await anonymous.Content.ReadAsStringAsync(ct);
        anonymousHtml.Should().Contain("Volunteer-scoped copy.");
        anonymousHtml.Should().NotContain("Board-scoped copy.");
        anonymousHtml.Should().NotContain("Refresh from GitHub",
            "the refresh form is AdminOnly and the viewer is anonymous");

        // Admin: sees every block, and the AdminOnly refresh form.
        await Factory.SignInAsFullyOnboardedAsync(Client, DevPersona.Admin);
        var admin = await Client.GetAsync("/Guide", ct);
        admin.StatusCode.Should().Be(HttpStatusCode.OK);
        var adminHtml = await admin.Content.ReadAsStringAsync(ct);
        adminHtml.Should().Contain("Volunteer-scoped copy.");
        adminHtml.Should().Contain("Board-scoped copy.");
        adminHtml.Should().Contain("Refresh from GitHub");
    }

    [HumansFact(Timeout = 120000)]
    public async Task An_unknown_stem_renders_the_sections_not_found_view()
    {
        // 404 with a body, not the generic error page: the status code alone would pass with
        // the section's Views/Guide/NotFound.cshtml missing entirely.
        var ct = Xunit.TestContext.Current.CancellationToken;

        var response = await Client.GetAsync("/Guide/NoSuchPage", ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var html = await response.Content.ReadAsStringAsync(ct);
        html.Should().Contain("That guide page does not exist");
        html.Should().NotContain("<vc:");
    }
}
