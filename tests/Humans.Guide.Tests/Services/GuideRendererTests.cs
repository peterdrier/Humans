using AwesomeAssertions;
using Xunit;
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

    [HumansFact]
    public void Render_AttributedAppPathCode_IsLinked()
    {
        var html = CreateRenderer().Render("`/Profile/Me`{.route}");

        html.Should().Contain("href=\"/Profile/Me\"");
        html.Should().Contain("<code class=\"route\">/Profile/Me</code>");
    }

    [HumansFact]
    public void Render_ImageShortPath_RewrittenToRawUrl()
    {
        const string markdown = "![x](img/screenshot.png)";

        var html = CreateRenderer().Render(markdown);

        html.Should().Contain("raw.githubusercontent.com/nobodies-collective/Humans/main/docs/guide/img/screenshot.png");
    }

    [HumansTheory]
    [InlineData("[`/Profile/Me`](Profiles.md)", "href=\"/Guide/Profiles\"")]
    [InlineData("<pre><code>/Profile/Me</code></pre>", "<pre><code>")]
    public void Render_AppPathInLinkOrCode_IsNotAutolinked(string markdown, string expected)
    {
        var html = CreateRenderer().Render(markdown);

        html.Should().Contain(expected);
        html.Should().Contain("/Profile/Me");
        html.Should().NotContain("guide-app-path");
    }

    [HumansFact]
    public void Render_CarriesNoRoleMarkupAtAll()
    {
        // The role model must not survive into the HTML as data-guide-* attributes; if one
        // ever comes back, the filter has grown a second home.
        const string markdown = """
            ## As a Coordinator (Consent Admin)

            Coordinator stuff.
            """;

        var html = CreateRenderer().Render(markdown);

        html.Should().NotContain("data-guide-role");
        html.Should().NotContain("data-guide-roles");
    }
}
