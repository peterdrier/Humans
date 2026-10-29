using System.Net;
using AwesomeAssertions;
using Humans.Guide.Services;
using Humans.Integration.Tests.Infrastructure;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Integration.Tests.Controllers;

/// <summary>Renders every Guide page through the real app.</summary>
/// <remarks>
/// <para>
/// No resource set or static assets. Without the section's <c>@@addTagHelper</c>, <c>AuthorizeViewTagHelper</c> leaves
/// <c>authorize-policy="AdminOnly"</c> as a literal attribute and the refresh form renders to everyone; asserting the
/// attribute is absent is the only catch. A sidebar entry proves the section's own <c>_GuideLayout</c> resolved.
/// </para>
/// <para>
/// The fixture writes segmented documents straight into <c>IMemoryCache</c>, so pages never fetch from GitHub and
/// can't fail on its rate limit.
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
        var cache = Factory.Services.GetRequiredService<IMemoryCache>();
        var segmenter = Factory.Services.GetRequiredService<GuideSegmenter>();

        foreach (var stem in stems)
        {
            // Mirrors GuideContentService's private cache key. The cache holds the segmented document: the role filter
            // runs before Markdig, per request.
            cache.Set($"guide:{stem}", segmenter.Segment(Markdown));
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

            html.Should().NotContain("authorize-policy",
                $"GET {url} left Humans.UI's authorize-policy tag helper unbound");
        }
    }

    [HumansFact(Timeout = 120000)]
    public async Task Role_scoped_blocks_are_filtered_per_viewer()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;

        // Anonymous sees Volunteer blocks only, which also proves the controller routes without a principal.
        SeedRenderedPages("README");
        var anonymous = await Client.GetAsync("/Guide", ct);
        anonymous.StatusCode.Should().Be(HttpStatusCode.OK);
        var anonymousHtml = await anonymous.Content.ReadAsStringAsync(ct);
        anonymousHtml.Should().Contain("Volunteer-scoped copy.");
        anonymousHtml.Should().NotContain("Board-scoped copy.");
        anonymousHtml.Should().NotContain("Refresh from GitHub",
            "the refresh form is AdminOnly and the viewer is anonymous");

        // Admin sees every block, and the AdminOnly refresh form.
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
        // A 404 with a body: the status alone would pass with Views/Guide/NotFound.cshtml missing.
        var ct = Xunit.TestContext.Current.CancellationToken;

        var response = await Client.GetAsync("/Guide/NoSuchPage", ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var html = await response.Content.ReadAsStringAsync(ct);
        html.Should().Contain("That guide page does not exist");
    }
}
