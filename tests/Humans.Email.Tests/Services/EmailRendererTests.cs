using AwesomeAssertions;
using Humans.Base.Configuration;
using Humans.Email.Services;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Humans.Email.Tests.Services;

public sealed class EmailRendererTests
{
    [HumansFact]
    public void FacilitatedMessage_renders_markdown_instead_of_html_encoded_plain_text()
    {
        var renderer = CreateRenderer();

        var content = renderer.RenderFacilitatedMessage(
            "Recipient",
            "Sender",
            "**Hi there** — see [this](https://example.com)\r\n\r\nSecond line",
            false,
            null);

        content.HtmlBody.Should().Contain("<p><strong>Hi there</strong>");
        content.HtmlBody.Should().Contain("<a href=\"https://example.com\">this</a>");
        content.HtmlBody.Should().Contain("<p>Second line</p>");
        content.HtmlBody.Should().NotContain("&lt;strong&gt;");
        content.HtmlBody.Should().NotContain("<br");
    }

    private static EmailRenderer CreateRenderer()
    {
        var strings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Email_FacilitatedMessage_Body"] =
                "<p>Hi {0},</p><p>{1} sent you a message:</p>{2}{3}",
        };
        var localizer = Substitute.For<IStringLocalizer<EmailResource>>();
        localizer[Arg.Any<string>()].Returns(call =>
        {
            var key = call.Arg<string>();
            return new LocalizedString(key, strings.GetValueOrDefault(key, key));
        });

        return new EmailRenderer(
            Options.Create(new EmailSettings { BaseUrl = "https://humans.example" }),
            localizer,
            NullLogger<EmailRenderer>.Instance);
    }
}
