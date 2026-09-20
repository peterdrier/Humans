using AwesomeAssertions;
using Humans.Email.Services;

namespace Humans.Email.Tests.Services;

public sealed class EmailInlineStylerTests
{
    [HumansFact]
    public void Apply_stamps_inline_style_on_every_supported_tag()
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
    public void Apply_preserves_and_appends_to_an_existing_style_attribute()
    {
        var html = "<p style=\"color:red;\">Hi</p>";

        var result = EmailInlineStyler.Apply(html);

        result.Should().Contain("color:red;");
        result.Should().Contain("margin:0 0 12px 0;");
    }

    [HumansFact]
    public void Apply_leaves_unstyled_tags_alone()
    {
        var html = "<p>Hi <em>there</em></p>";

        var result = EmailInlineStyler.Apply(html);

        result.Should().Contain("<em>there</em>");
    }

    [HumansFact]
    public void Apply_returns_input_unchanged_when_empty()
    {
        EmailInlineStyler.Apply(string.Empty).Should().BeEmpty();
    }
}
