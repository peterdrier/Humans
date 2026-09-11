using AwesomeAssertions;
using Microsoft.Extensions.Options;
using Humans.Base.Configuration;
using Humans.Guide.Services;

namespace Humans.Guide.Tests.Services;

public class GuideRendererTests
{
    private static readonly GuideSettings Settings = new()
    {
        Owner = "nobodies-collective",
        Repository = "Humans",
        Branch = "main",
        FolderPath = "docs/guide"
    };

    private static GuideRenderer CreateRenderer() => new(
        Options.Create(Settings),
        new GuideHtmlPostprocessor());

    // These four run markdown through the real Markdig pipeline rather than calling the
    // postprocessor directly, which is the only thing pinning that Markdig's output is still
    // the shape the postprocessor's regexes expect — HrefPattern wants a double-quoted href,
    // and nothing else would notice if that stopped being true.

    [HumansFact]
    public void Render_SiblingMdLink_RewrittenToGuideRoute()
    {
        const string markdown = "See [Profiles](Profiles.md) for details.";

        var html = CreateRenderer().Render(markdown, "Teams");

        html.Should().Contain("/Guide/Profiles");
    }

    [HumansFact]
    public void Render_ImageShortPath_RewrittenToRawUrl()
    {
        const string markdown = "![x](img/screenshot.png)";

        var html = CreateRenderer().Render(markdown, "Profiles");

        html.Should().Contain("raw.githubusercontent.com/nobodies-collective/Humans/main/docs/guide/img/screenshot.png");
    }

    [HumansFact]
    public void Render_ExternalLink_GetsBlankTarget()
    {
        const string markdown = "[ex](https://example.com)";

        var html = CreateRenderer().Render(markdown, "Profiles");

        html.Should().Contain("target=\"_blank\"");
    }

    [HumansFact]
    public void Render_AppPathLink_LeftAsIs()
    {
        const string markdown = "[Edit](/Profile/Me/Edit)";

        var html = CreateRenderer().Render(markdown, "Profiles");

        html.Should().Contain("/Profile/Me/Edit");
        html.Should().NotContain("target=\"_blank\"");
    }

    [HumansFact]
    public void Render_CarriesNoRoleMarkupAtAll()
    {
        // The role model used to survive into the HTML as data-guide-* attributes, because the
        // filter ran after rendering. Filtering markdown deleted that round trip; if an
        // attribute ever comes back, the filter has grown a second home.
        const string markdown = """
            ## As a Coordinator (Consent Coordinator)

            Coordinator stuff.
            """;

        var html = CreateRenderer().Render(markdown, "Profiles");

        html.Should().NotContain("data-guide-role");
        html.Should().NotContain("data-guide-roles");
    }
}
