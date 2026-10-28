using AwesomeAssertions;
using AngleSharp.Html.Parser;
using Humans.Base.Configuration;
using Humans.Guide.Services;
using Xunit;

namespace Humans.Guide.Tests.Services;

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

        var result = Processor.Rewrite(html, Settings);

        result.Should().Contain(expectedHrefSnippet);
    }

    [HumansTheory]
    [InlineData("NonExistent.md", "https://github.com/nobodies-collective/Humans/blob/main/docs/guide/NonExistent.md")]
    [InlineData("https://example.com/foo", "https://example.com/foo")]
    [InlineData("../sections/Teams.md", "https://github.com/nobodies-collective/Humans/blob/main/docs/sections/Teams.md")]
    public void Rewrite_ExternalHref_GetsNewTabAttrs(string href, string expectedHrefSubstring)
    {
        var html = $"""<a href="{href}">x</a>""";

        var result = Processor.Rewrite(html, Settings);

        result.Should().Contain(expectedHrefSubstring);
        result.Should().Contain("target=\"_blank\"");
        result.Should().Contain("rel=\"noopener\"");
    }

    [HumansTheory]
    [InlineData("<a href='Profiles.md'>Profiles</a><img src='img/screenshot.png'>")]
    [InlineData("<a href=Profiles.md>Profiles</a><img src=img/screenshot.png>")]
    public void Rewrite_RawHtmlUrls_UseTheSameRoutesAsMarkdown(string html)
    {
        using var document = new HtmlParser().ParseDocument(Processor.Rewrite(html, Settings));

        document.QuerySelector("a")!.GetAttribute("href").Should().Be("/Guide/Profiles");
        document.QuerySelector("img")!.GetAttribute("src").Should().Be(
            "https://raw.githubusercontent.com/nobodies-collective/Humans/main/docs/guide/img/screenshot.png");
    }

    [HumansTheory]
    [InlineData("<a target=\"_blank\" rel=\"noreferrer\" href=\"https://example.com\">x</a>")]
    [InlineData("<a href=\"https://example.com\" target=\"_blank\" rel=\"noreferrer\">x</a>")]
    public void Rewrite_ExternalBlankTarget_PreservesExistingRelAndAddsNoopener(string html)
    {
        using var document = new HtmlParser().ParseDocument(Processor.Rewrite(html, Settings));
        var link = document.QuerySelector("a")!;

        link.GetAttribute("target").Should().Be("_blank");
        link.GetAttribute("rel")!.Split(' ').Should().BeEquivalentTo(["noreferrer", "noopener"]);
    }

    [HumansFact]
    public void Rewrite_LinkInFragment_PreservesStylesCommentsAndAuthoredTarget()
    {
        const string html = """<style>.guide { color: red; }</style><!-- guide --><a target="_self" href="NonExistent.md">x</a>""";

        var result = Processor.Rewrite(html, Settings);

        result.Should().Contain("<style>.guide { color: red; }</style>");
        result.Should().Contain("<!-- guide -->");
        using var document = new HtmlParser().ParseDocument(result);
        var link = document.QuerySelector("a")!;
        link.GetAttribute("href").Should().Be(
            "https://github.com/nobodies-collective/Humans/blob/main/docs/guide/NonExistent.md");
        link.GetAttribute("target").Should().Be("_self");
    }

    [HumansFact]
    public void Rewrite_AppPathLink_LeftAsIs()
    {
        const string html = """<a href="/Profile/Me/Edit">Edit</a>""";

        var result = Processor.Rewrite(html, Settings);

        result.Should().Contain("""href="/Profile/Me/Edit" """.Trim());
        result.Should().NotContain("target=\"_blank\"");
    }

    [HumansFact]
    public void Rewrite_MailtoLink_LeftAsIs()
    {
        const string html = """<a href="mailto:a@b.com">a</a>""";

        var result = Processor.Rewrite(html, Settings);

        result.Should().Contain("""href="mailto:a@b.com" """.Trim());
        result.Should().NotContain("target=\"_blank\"");
    }

    // Both rows must produce the same raw URL: row 2 already starts with the guide folder, and
    // without prefix-stripping the result would carry a doubled "docs/guide/" segment.
    [HumansTheory]
    [InlineData("img/screenshot.png")]
    [InlineData("docs/guide/img/screenshot.png")]
    public void Rewrite_ImageShortOrPrefixedPath_BecomesRawGitHubUrl(string src)
    {
        var html = $"""<img src="{src}" alt="x" />""";

        var result = Processor.Rewrite(html, Settings);

        result.Should().Contain("""src="https://raw.githubusercontent.com/nobodies-collective/Humans/main/docs/guide/img/screenshot.png" """.Trim());
    }

    [HumansFact]
    public void Rewrite_ImageAbsoluteUrl_LeftAsIs()
    {
        const string html = """<img src="https://cdn.example.com/x.png" alt="x" />""";

        var result = Processor.Rewrite(html, Settings);

        result.Should().Contain("""src="https://cdn.example.com/x.png" """.Trim());
    }

    [HumansTheory]
    [InlineData("<p>Go to <code>/Profile/Me</code> to view your profile.</p>", "/Profile/Me")]
    [InlineData("<code>/Profile/Me/Edit</code>", "/Profile/Me/Edit")]
    public void Rewrite_InlineCodeAppPath_WrappedInAnchor(string html, string path)
    {
        var result = Processor.Rewrite(html, Settings);

        result.Should().Contain($"""<a href="{path}" class="guide-app-path"><code>{path}</code></a>""");
    }

    [HumansFact]
    public void Rewrite_InlineCodeRouteTemplate_LeftAsIs()
    {
        // Routes with "{id}" placeholders should NOT be linked — clicking /Users/Admin/{id} would 404.
        const string html = "<code>/Users/Admin/{id}</code>";

        var result = Processor.Rewrite(html, Settings);

        result.Should().NotContain("<a href=");
        result.Should().Contain("<code>/Users/Admin/{id}</code>");
    }

    [HumansFact]
    public void Rewrite_InlineCodeWildcardPath_LeftAsIs()
    {
        // docs/guide/Admin.md ships `/api/backdoor/*`; linking it would 404 the same way a
        // route template would.
        const string html = "<code>/api/backdoor/*</code>";

        var result = Processor.Rewrite(html, Settings);

        result.Should().NotContain("<a href=");
        result.Should().Contain("<code>/api/backdoor/*</code>");
    }

    [HumansFact]
    public void Rewrite_InlineCodeNonPath_LeftAsIs()
    {
        // Not a path (doesn't start with "/") — it's a config key or a literal value.
        const string html = "<p>Set <code>Guide:Owner</code> to your fork.</p>";

        var result = Processor.Rewrite(html, Settings);

        result.Should().NotContain("<a href=");
        result.Should().Contain("<code>Guide:Owner</code>");
    }
}
