using AwesomeAssertions;
using Microsoft.Extensions.Options;
using Humans.Infrastructure.Configuration;
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
        new GuideMarkdownPreprocessor(),
        new GuideHtmlPostprocessor());

    [HumansFact]
    public void Render_RoleSection_WrappedWithDiv()
    {
        const string markdown = """
            # Profiles

            Intro.

            ## As a Volunteer

            Do stuff.
            """;

        var html = CreateRenderer().Render(markdown, "Profiles");

        html.Should().Contain("<div data-guide-role=\"volunteer\"");
    }

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
    public void Render_GlossaryFile_NoRoleWrappers()
    {
        const string markdown = """
            # Glossary

            ## Admin

            A human with full access.
            """;

        var html = CreateRenderer().Render(markdown, "Glossary");

        html.Should().NotContain("data-guide-role");
    }

    [HumansFact]
    public void Render_ShippedContent_ProducesNoDivNestedInsideARoleBlock()
    {
        // GuideFilter matches a role block with a non-greedy .*?</div>, so it stops at the
        // FIRST closing tag. A <div> emitted inside a block — Markdig produces them for
        // footnotes and ::: custom containers — would end the match early and leave the rest
        // of that block unwrapped, i.e. visible to everyone. The filter's comment asserts
        // guide markdown has no nested divs; this checks that against what actually ships.
        var renderer = CreateRenderer();
        var offenders = new List<string>();

        foreach (var file in Directory.GetFiles(Path.Combine(LocateRepoRoot(), "docs", "guide"), "*.md"))
        {
            var stem = Path.GetFileNameWithoutExtension(file);
            var html = renderer.Render(File.ReadAllText(file), stem);

            foreach (var blockStart in RoleDivOffsets(html))
            {
                var close = html.IndexOf("</div>", blockStart, StringComparison.Ordinal);
                var body = close < 0 ? html[blockStart..] : html[blockStart..close];
                if (body.Contains("<div", StringComparison.Ordinal))
                {
                    offenders.Add(stem);
                }
            }
        }

        offenders.Should().BeEmpty(
            "a nested div truncates the role block GuideFilter strips, leaking the tail of a "
            + "coordinator or board/admin section to anonymous readers");
    }

    private static IEnumerable<int> RoleDivOffsets(string html)
    {
        const string marker = "<div data-guide-role=";
        var at = html.IndexOf(marker, StringComparison.Ordinal);
        while (at >= 0)
        {
            yield return at + marker.Length;
            at = html.IndexOf(marker, at + marker.Length, StringComparison.Ordinal);
        }
    }

    private static string LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Humans.slnx")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException(
            "Could not locate repository root (no Humans.slnx above " + AppContext.BaseDirectory + ").");
    }
}
