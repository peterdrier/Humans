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

    [HumansFact]
    public void Render_allows_https_images()
    {
        var html = SanitizedMarkdownRenderer.Render("![poster](https://example.com/poster.png)");

        html.Should().Contain("<img");
        html.Should().Contain("https://example.com/poster.png");
    }

    [HumansFact]
    public void Render_strips_http_image_sources()
    {
        var html = SanitizedMarkdownRenderer.Render("![poster](http://example.com/poster.png)");

        html.Should().NotContain("poster.png");
    }

    [HumansFact]
    public void Render_strips_data_uri_image_sources()
    {
        var html = SanitizedMarkdownRenderer.Render(
            "![poster](data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=)");

        html.Should().NotContain("data:image");
    }

    [HumansFact]
    public void Render_strips_javascript_uri_image_sources()
    {
        var html = SanitizedMarkdownRenderer.Render(
            "<img src=\"javascript:alert(1)\" onerror=\"alert(1)\">");

        html.Should().NotContain("javascript:");
        html.Should().NotContain("onerror");
    }

    [HumansFact]
    public void Render_still_allows_http_links()
    {
        var html = SanitizedMarkdownRenderer.Render("[web](http://example.com)");

        html.Should().Contain("href=\"http://example.com\"");
    }

    [HumansFact]
    public void Render_strips_style_attributes()
    {
        var html = SanitizedMarkdownRenderer.Render("<p style=\"color:red\">Body</p>");

        html.Should().NotContain("style=");
        html.Should().Contain("Body");
    }

    [HumansFact]
    public void Render_strips_style_attribute_tracking_pixel_via_css_url()
    {
        var html = SanitizedMarkdownRenderer.Render(
            "<p style=\"background-image:url(http://tracker.example.com/pixel.png)\">Body</p>");

        html.Should().NotContain("style=");
        html.Should().NotContain("tracker.example.com");
    }

    [HumansFact]
    public void Render_strips_http_image_source_on_input_type_image()
    {
        var html = SanitizedMarkdownRenderer.Render(
            "<input type=\"image\" src=\"http://tracker.example.com/pixel.png\">");

        html.Should().NotContain("tracker.example.com");
    }
}
