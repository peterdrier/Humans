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

    [Fact]
    public void Rewrite_SiblingMdLink_BecomesGuideRoute()
    {
        const string html = """<a href="Profiles.md">Profiles</a>""";

        var result = Processor.Rewrite(html, Settings, GuideFiles.All);

        result.Should().Contain("""href="/Guide/Profiles" """.Trim());
    }

    [Fact]
    public void Rewrite_SiblingMdWithFragment_PreservesFragment()
    {
        const string html = """<a href="Glossary.md#coordinator">Coordinator</a>""";

        var result = Processor.Rewrite(html, Settings, GuideFiles.All);

        result.Should().Contain("""href="/Guide/Glossary#coordinator" """.Trim());
    }

    [Fact]
    public void Rewrite_SiblingMdCaseInsensitive_MatchesKnown()
    {
        const string html = """<a href="profiles.md">Profiles</a>""";

        var result = Processor.Rewrite(html, Settings, GuideFiles.All);

        result.Should().Contain("/Guide/Profiles");
    }

    [Fact]
    public void Rewrite_UnknownSiblingMd_LeftAsExternal()
    {
        const string html = """<a href="NonExistent.md">Link</a>""";

        var result = Processor.Rewrite(html, Settings, GuideFiles.All);

        // Unknown siblings fall through to external github.com URL.
        result.Should().Contain("https://github.com/nobodies-collective/Humans/blob/main/docs/guide/NonExistent.md");
        result.Should().Contain("target=\"_blank\"");
    }

    [Fact]
    public void Rewrite_AppPathLink_LeftAsIs()
    {
        const string html = """<a href="/Profile/Me/Edit">Edit</a>""";

        var result = Processor.Rewrite(html, Settings, GuideFiles.All);

        result.Should().Contain("""href="/Profile/Me/Edit" """.Trim());
        result.Should().NotContain("target=\"_blank\"");
    }

    [Fact]
    public void Rewrite_ExternalHttpLink_GetsNewTabAttrs()
    {
        const string html = """<a href="https://example.com/foo">x</a>""";

        var result = Processor.Rewrite(html, Settings, GuideFiles.All);

        result.Should().Contain("target=\"_blank\"");
        result.Should().Contain("rel=\"noopener\"");
    }

    [Fact]
    public void Rewrite_MailtoLink_LeftAsIs()
    {
        const string html = """<a href="mailto:a@b.com">a</a>""";

        var result = Processor.Rewrite(html, Settings, GuideFiles.All);

        result.Should().Contain("""href="mailto:a@b.com" """.Trim());
        result.Should().NotContain("target=\"_blank\"");
    }

    [Fact]
    public void Rewrite_ParentRelativePath_BecomesGitHubBlobUrl()
    {
        const string html = """<a href="../sections/Teams.md">Section invariants</a>""";

        var result = Processor.Rewrite(html, Settings, GuideFiles.All);

        result.Should().Contain("https://github.com/nobodies-collective/Humans/blob/main/docs/sections/Teams.md");
        result.Should().Contain("target=\"_blank\"");
    }

    [Fact]
    public void Rewrite_ImageShortPath_BecomesRawGitHubUrl()
    {
        const string html = """<img src="img/screenshot.png" alt="x" />""";

        var result = Processor.Rewrite(html, Settings, GuideFiles.All);

        result.Should().Contain("""src="https://raw.githubusercontent.com/nobodies-collective/Humans/main/docs/guide/img/screenshot.png" """.Trim());
    }

    [Fact]
    public void Rewrite_ImageWithDocsGuidePrefix_AlsoRewritten()
    {
        const string html = """<img src="docs/guide/img/screenshot.png" alt="x" />""";

        var result = Processor.Rewrite(html, Settings, GuideFiles.All);

        result.Should().Contain("https://raw.githubusercontent.com/nobodies-collective/Humans/main/docs/guide/img/screenshot.png");
    }

    [Fact]
    public void Rewrite_ImageAbsoluteUrl_LeftAsIs()
    {
        const string html = """<img src="https://cdn.example.com/x.png" alt="x" />""";

        var result = Processor.Rewrite(html, Settings, GuideFiles.All);

        result.Should().Contain("""src="https://cdn.example.com/x.png" """.Trim());
    }

    [Fact]
    public void Rewrite_InlineCodeAppPath_WrappedInAnchor()
    {
        const string html = "<p>Go to <code>/Profile/Me</code> to view your profile.</p>";

        var result = Processor.Rewrite(html, Settings, GuideFiles.All);

        result.Should().Contain("""<a href="/Profile/Me" class="guide-app-path"><code>/Profile/Me</code></a>""");
    }

    [Fact]
    public void Rewrite_InlineCodeAppPathWithSegments_WrappedInAnchor()
    {
        const string html = "<code>/Profile/Me/Edit</code>";

        var result = Processor.Rewrite(html, Settings, GuideFiles.All);

        result.Should().Contain("""href="/Profile/Me/Edit" """.Trim());
    }

    [Fact]
    public void Rewrite_InlineCodeRouteTemplate_LeftAsIs()
    {
        // Routes with "{id}" placeholders should NOT be linked — clicking /Profile/{id} would 404.
        const string html = "<code>/Profile/{id}/Admin</code>";

        var result = Processor.Rewrite(html, Settings, GuideFiles.All);

        result.Should().NotContain("<a href=");
        result.Should().Contain("<code>/Profile/{id}/Admin</code>");
    }

    [Fact]
    public void Rewrite_InlineCodeNonPath_LeftAsIs()
    {
        // Not a path (doesn't start with "/") — it's a config key or a literal value.
        const string html = "<p>Set <code>Guide:Owner</code> to your fork.</p>";

        var result = Processor.Rewrite(html, Settings, GuideFiles.All);

        result.Should().NotContain("<a href=");
        result.Should().Contain("<code>Guide:Owner</code>");
    }
}
