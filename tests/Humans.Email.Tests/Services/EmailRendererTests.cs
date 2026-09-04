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
    public void SurveyInvitation_custom_copy_renders_sanitized_markdown_without_images()
    {
        var renderer = CreateRenderer();

        var content = renderer.RenderSurveyInvitation(
            "Daniel <Admin>",
            "Dates & places",
            "token + value",
            "en",
            "  Help choose our dates  ",
            "  **Choose carefully.**\r\n\r\n- Friday\r\n- Saturday\r\n\r\n[Details](https://example.com)\r\n\r\n![Poster](https://example.com/poster.png)\r\n<script>alert('x')</script>  ");

        content.Subject.Should().Be("Help choose our dates");
        content.HtmlBody.Should().Contain("Daniel &lt;Admin&gt;");
        content.HtmlBody.Should().Contain("<h2>Dates &amp; places</h2>");
        content.HtmlBody.Should().Contain("<p><strong>Choose carefully.</strong></p>");
        content.HtmlBody.Should().Contain("<ul>");
        content.HtmlBody.Should().Contain("<li>Friday</li>");
        content.HtmlBody.Should().Contain("<a href=\"https://example.com\">Details</a>");
        content.HtmlBody.Should().NotContain("<script>");
        content.HtmlBody.Should().NotContain("<img");
        content.HtmlBody.Should().NotContain("poster.png");
        content.HtmlBody.Should().Contain(
            "https://humans.example/Survey/Answer?t=token%20%2B%20value");
    }

    [HumansFact]
    public void SurveyInvitation_blank_custom_copy_retains_standard_localized_wording()
    {
        var renderer = CreateRenderer();

        var content = renderer.RenderSurveyInvitation(
            "Daniel", "Test Survey", "token", "en", " ", null);

        content.Subject.Should().Be("Please complete: Test Survey");
        content.HtmlBody.Should().Contain(
            "<p>You're invited to complete <strong>Test Survey</strong>.</p>");
    }

    [HumansFact]
    public void FeedbackResponse_renders_sanitized_markdown_without_images()
    {
        var renderer = CreateRenderer();

        var content = renderer.RenderFeedbackResponse(
            "Daniel <Admin>",
            "The <b>lights</b> were off",
            "**Fixed.**\r\n\r\n[Details](https://example.com)\r\n\r\n![Shot](https://example.com/shot.png)\r\n<script>alert('x')</script>",
            "/Feedback/12",
            "en");

        content.HtmlBody.Should().Contain("Daniel &lt;Admin&gt;");
        content.HtmlBody.Should().Contain("The &lt;b&gt;lights&lt;/b&gt; were off");
        content.HtmlBody.Should().Contain("<p><strong>Fixed.</strong></p>");
        content.HtmlBody.Should().Contain("<a href=\"https://example.com\">Details</a>");
        content.HtmlBody.Should().NotContain("<script>");
        content.HtmlBody.Should().NotContain("<img");
        content.HtmlBody.Should().NotContain("shot.png");
    }

    [HumansFact]
    public void IssueComment_renders_sanitized_markdown_without_images()
    {
        var renderer = CreateRenderer();

        var content = renderer.RenderIssueComment(
            "Daniel <Admin>",
            "Lights & sound",
            "**Looking at it.**\r\n\r\n[Details](https://example.com)\r\n\r\n![Shot](https://example.com/shot.png)\r\n<script>alert('x')</script>",
            "/Issues/12",
            "en");

        content.HtmlBody.Should().Contain("Daniel &lt;Admin&gt;");
        content.HtmlBody.Should().Contain("Lights &amp; sound");
        content.HtmlBody.Should().Contain("<p><strong>Looking at it.</strong></p>");
        content.HtmlBody.Should().Contain("<a href=\"https://example.com\">Details</a>");
        content.HtmlBody.Should().NotContain("<script>");
        content.HtmlBody.Should().NotContain("<img");
        content.HtmlBody.Should().NotContain("shot.png");
    }

    private static EmailRenderer CreateRenderer()
    {
        var strings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Email_SurveyInvitation_Subject"] = "Please complete: {0}",
            ["Email_SurveyInvitation_DefaultMessage"] = "You're invited to complete <strong>{0}</strong>.",
            ["Email_SurveyInvitation_Body"] =
                "<h2>{1}</h2><p>Hi {0},</p>{3}<p><a href=\"{2}\">Open the survey</a></p>",
            ["Email_FeedbackResponse_Body"] =
                "<p>Hi {0},</p><blockquote>{1}</blockquote>{2}<p><a href=\"{3}\">Open the report</a></p>",
            ["Email_IssueComment_Subject"] = "New comment on {0}",
            ["Email_IssueComment_Body"] =
                "<p>Hi {0},</p><h2>{1}</h2>{2}<p><a href=\"{3}\">Open the issue</a></p>",
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
