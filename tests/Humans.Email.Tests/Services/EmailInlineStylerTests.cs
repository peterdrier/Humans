using AwesomeAssertions;
using Humans.Email.Services;

namespace Humans.Email.Tests.Services;

public sealed class EmailInlineStylerTests
{
    [HumansFact]
    public void Apply_SupportedTags_GetInlineStyle()
    {
        var html = "<h1>H1</h1><h2>H2</h2><h3>H3</h3><p>P</p><a href=\"https://example.com\">A</a>"
            + "<ul><li>Li</li></ul><ol><li>Li</li></ol><blockquote>Quote</blockquote>"
            + "<table><tr><th>Th</th><td>Td</td></tr></table><hr /><code>Code</code><pre>Pre</pre>";

        var result = EmailInlineStyler.Apply(html);

        foreach (var tag in new[] { "h1", "h2", "h3", "p", "a", "ul", "ol", "li", "blockquote", "table", "th", "td", "hr", "code", "pre" })
        {
            result.Should().MatchRegex($"<{tag}[^>]*\\sstyle=\"[^\"]+\"", $"<{tag}> should carry an inline style");
        }
    }

    [HumansFact]
    public void Apply_ExistingStyleAttribute_WinsOverPalette()
    {
        var html = "<p style=\"color:red;\">Hi</p>";

        var result = EmailInlineStyler.Apply(html);

        result.Should().Contain("margin:0 0 12px 0;");
        // The element's own declarations must come last so they win: a template that already
        // colours (say) a CTA button keeps its colour.
        result.Should().MatchRegex("style=\"[^\"]*color:#3d2b1f;[^\"]*color:red;");
    }

    [HumansFact]
    public void Apply_UnstyledTags_AreLeftAlone()
    {
        var html = "<p>Hi <em>there</em></p>";

        var result = EmailInlineStyler.Apply(html);

        result.Should().Contain("<em>there</em>");
    }

    [HumansFact]
    public void Apply_EmptyInput_ReturnsUnchanged()
    {
        EmailInlineStyler.Apply(string.Empty).Should().BeEmpty();
    }
}
