using AwesomeAssertions;
using Humans.Email.Services;
using Humans.Users.Contracts;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Humans.Email.Tests.Services;

/// <summary>
/// Email's one remaining template, built by <see cref="EmailMessageFactory"/>: the
/// content it renders and the policy it stamps (template name, opt-out category,
/// reply-to). Every other section's templates are tested in that section's own test
/// project (peterdrier/Humans#1651). The shared transport (opt-out, unsubscribe,
/// wrapping, enqueue) is covered by <see cref="OutboxEmailServiceTests"/>.
/// </summary>
public sealed class EmailMessageFactoryTests
{
    private readonly EmailMessageFactory _factory = CreateFactory();

    [HumansFact]
    public void FacilitatedMessage_WithContactInfo_SetsReplyToSender()
    {
        var msg = _factory.FacilitatedMessage(
            "rcpt@x.com", "Rcpt", "Sender", "Hi", includeContactInfo: true, senderEmail: "sender@x.com", "en");

        msg.TemplateName.Should().Be("facilitated_message");
        msg.Category.Should().Be(MessageCategory.FacilitatedMessages);
        msg.ReplyTo.Should().Be("sender@x.com");
        msg.HtmlBody.Should().Contain("mailto:sender@x.com");
    }

    [HumansFact]
    public void FacilitatedMessage_WithoutContactInfo_HasNoReplyTo()
    {
        var msg = _factory.FacilitatedMessage(
            "rcpt@x.com", "Rcpt", "Sender", "Hi", includeContactInfo: false, senderEmail: "sender@x.com", "en");

        msg.ReplyTo.Should().BeNull();
        msg.HtmlBody.Should().Contain("Email_FacilitatedMessage_NoContactInfo");
    }

    [HumansFact]
    public void FacilitatedMessage_renders_markdown_instead_of_html_encoded_plain_text()
    {
        var msg = _factory.FacilitatedMessage(
            "rcpt@x.com", "Recipient", "Sender",
            "**Hi there** — see [this](https://example.com)\r\n\r\nSecond line",
            includeContactInfo: false, senderEmail: null);

        msg.HtmlBody.Should().Contain("<p><strong>Hi there</strong>");
        msg.HtmlBody.Should().Contain("<a href=\"https://example.com\">this</a>");
        msg.HtmlBody.Should().Contain("<p>Second line</p>");
        msg.HtmlBody.Should().NotContain("&lt;strong&gt;");
        msg.HtmlBody.Should().NotContain("<br");
    }

    private static EmailMessageFactory CreateFactory()
    {
        var strings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Email_FacilitatedMessage_Subject"] = "Humans Message from: {0}",
            ["Email_FacilitatedMessage_Body"] =
                "<p>Hi {0},</p><p>{1} sent you a message:</p>{2}{3}",
        };
        var localizer = Substitute.For<IStringLocalizer<EmailResource>>();
        localizer[Arg.Any<string>()].Returns(call =>
        {
            var key = call.Arg<string>();
            return new LocalizedString(key, strings.GetValueOrDefault(key, key));
        });

        return new EmailMessageFactory(localizer, NullLogger<EmailMessageFactory>.Instance);
    }
}
