using AwesomeAssertions;
using Humans.Base.Extensions;

namespace Humans.Base.Tests.Extensions;

public sealed class SanitizedMarkdownRendererTests
{
    [HumansFact]
    public void Render_returns_empty_for_blank_markdown()
    {
        SanitizedMarkdownRenderer.Render(" ").Should().BeEmpty();
    }

    [HumansFact]
    public void Render_preserves_markdown_and_removes_unsafe_html()
    {
        var html = SanitizedMarkdownRenderer.Render(
            "**Important**\n\n- First\n- Second\n\n<script>alert('x')</script>");

        html.Should().Contain("<strong>Important</strong>");
        html.Should().Contain("<li>First</li>");
        html.Should().NotContain("<script>");
    }

    [HumansFact]
    public void Render_can_remove_images_for_email_content()
    {
        var html = SanitizedMarkdownRenderer.Render(
            "Before ![poster](https://example.com/poster.png) after",
            allowImages: false);

        html.Should().NotContain("<img");
        html.Should().NotContain("poster.png");
    }
}
