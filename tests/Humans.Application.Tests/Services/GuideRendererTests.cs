using AwesomeAssertions;
using Microsoft.Extensions.Options;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Services;
using Humans.Testing;
using Xunit;

namespace Humans.Application.Tests.Services;

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
}
