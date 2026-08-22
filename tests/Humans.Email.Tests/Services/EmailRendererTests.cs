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
    public void SurveyInvitation_custom_copy_is_trimmed_encoded_and_preserves_line_breaks()
    {
        var renderer = CreateRenderer();

        var content = renderer.RenderSurveyInvitation(
            "Daniel <Admin>",
            "Dates & places",
            "token + value",
            "en",
            "  Help choose our dates  ",
            "  First line\r\n<script>alert('x')</script>\nLast line  ");

        content.Subject.Should().Be("Help choose our dates");
        content.HtmlBody.Should().Contain("Daniel &lt;Admin&gt;");
        content.HtmlBody.Should().Contain("<h2>Dates &amp; places</h2>");
        content.HtmlBody.Should().Contain(
            "<p>First line<br />&lt;script&gt;alert(&#39;x&#39;)&lt;/script&gt;<br />Last line</p>");
        content.HtmlBody.Should().NotContain("<script>");
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

    private static EmailRenderer CreateRenderer()
    {
        var strings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Email_SurveyInvitation_Subject"] = "Please complete: {0}",
            ["Email_SurveyInvitation_DefaultMessage"] = "You're invited to complete <strong>{0}</strong>.",
            ["Email_SurveyInvitation_Body"] =
                "<h2>{1}</h2><p>Hi {0},</p>{3}<p><a href=\"{2}\">Open the survey</a></p>",
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
