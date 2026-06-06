using AwesomeAssertions;
using Humans.Application.Constants;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Services;
using Xunit;

namespace Humans.Application.Tests.Services;

public class GuideHtmlPostprocessorTests
{
    private static readonly GuideSettings Settings = new()
    {
        Owner = "nobodies-collective",
        Repository = "Humans",
        Branch = "main",
        FolderPath = "docs/guide"
    };

    private static readonly GuideHtmlPostprocessor Processor = new();

    [HumansTheory]
    [InlineData("Profiles.md", "href=\"/Guide/Profiles\"")]
    [InlineData("Glossary.md#coordinator", "href=\"/Guide/Glossary#coordinator\"")]
    [InlineData("profiles.md", "href=\"/Guide/Profiles\"")]
    public void Rewrite_KnownSiblingMdLink_MapsToGuideRoute(string href, string expectedHrefSnippet)
    {
        var html = $"""<a href="{href}">text</a>""";

        var result = Processor.Rewrite(html, Settings, GuideFiles.All);

        result.Should().Contain(expectedHrefSnippet);
    }

    [HumansTheory]
    [InlineData("NonExistent.md", "https://github.com/nobodies-collective/Humans/blob/main/docs/guide/NonExistent.md")]
    [InlineData("https://example.com/foo", "https://example.com/foo")]
    [InlineData("../sections/Teams.md", "https://github.com/nobodies-collective/Humans/blob/main/docs/sections/Teams.md")]
    public void Rewrite_ExternalHref_GetsNewTabAttrs(string href, string expectedHrefSubstring)
    {
        var html = $"""<a href="{href}">x</a>""";

        var result = Processor.Rewrite(html, Settings, GuideFiles.All);

        result.Should().Contain(expectedHrefSubstring);
        result.Should().Contain("target=\"_blank\"");
        result.Should().Contain("rel=\"noopener\"");
    }

    [HumansFact]
    public void Rewrite_AppPathLink_LeftAsIs()
    {
        const string html = """<a href="/Profile/Me/Edit">Edit</a>""";

        var result = Processor.Rewrite(html, Settings, GuideFiles.All);

        result.Should().Contain("""href="/Profile/Me/Edit" """.Trim());
        result.Should().NotContain("target=\"_blank\"");
    }

    [HumansFact]
    public void Rewrite_MailtoLink_LeftAsIs()
    {
        const string html = """<a href="mailto:a@b.com">a</a>""";

        var result = Processor.Rewrite(html, Settings, GuideFiles.All);

        result.Should().Contain("""href="mailto:a@b.com" """.Trim());
        result.Should().NotContain("target=\"_blank\"");
    }

    // Both rows must produce the same raw URL — row 2 exercises the
    // prefix-stripping branch in RewriteImgSrc (line ~137) where the input
    // already starts with the guide folder; without stripping, the result
    // would have a doubled "docs/guide/" segment.
    [HumansTheory]
    [InlineData("img/screenshot.png")]
    [InlineData("docs/guide/img/screenshot.png")]
    public void Rewrite_ImageShortOrPrefixedPath_BecomesRawGitHubUrl(string src)
    {
        var html = $"""<img src="{src}" alt="x" />""";

        var result = Processor.Rewrite(html, Settings, GuideFiles.All);

        result.Should().Contain("""src="https://raw.githubusercontent.com/nobodies-collective/Humans/main/docs/guide/img/screenshot.png" """.Trim());
    }

    [HumansFact]
    public void Rewrite_ImageAbsoluteUrl_LeftAsIs()
    {
        const string html = """<img src="https://cdn.example.com/x.png" alt="x" />""";

        var result = Processor.Rewrite(html, Settings, GuideFiles.All);

        result.Should().Contain("""src="https://cdn.example.com/x.png" """.Trim());
    }

    [HumansTheory]
    [InlineData("<p>Go to <code>/Profile/Me</code> to view your profile.</p>", "/Profile/Me")]
    [InlineData("<code>/Profile/Me/Edit</code>", "/Profile/Me/Edit")]
    public void Rewrite_InlineCodeAppPath_WrappedInAnchor(string html, string path)
    {
        var result = Processor.Rewrite(html, Settings, GuideFiles.All);

        result.Should().Contain($"""<a href="{path}" class="guide-app-path"><code>{path}</code></a>""");
    }

    [HumansFact]
    public void Rewrite_InlineCodeRouteTemplate_LeftAsIs()
    {
        // Routes with "{id}" placeholders should NOT be linked — clicking /Users/Admin/{id} would 404.
        const string html = "<code>/Users/Admin/{id}</code>";

        var result = Processor.Rewrite(html, Settings, GuideFiles.All);

        result.Should().NotContain("<a href=");
        result.Should().Contain("<code>/Users/Admin/{id}</code>");
    }

    [HumansFact]
    public void Rewrite_InlineCodeNonPath_LeftAsIs()
    {
        // Not a path (doesn't start with "/") — it's a config key or a literal value.
        const string html = "<p>Set <code>Guide:Owner</code> to your fork.</p>";

        var result = Processor.Rewrite(html, Settings, GuideFiles.All);

        result.Should().NotContain("<a href=");
        result.Should().Contain("<code>Guide:Owner</code>");
    }
}
