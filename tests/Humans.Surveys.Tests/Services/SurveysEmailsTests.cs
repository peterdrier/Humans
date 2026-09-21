using System.Reflection;
using AwesomeAssertions;
using Humans.Email.Contracts;
using Humans.Surveys.Services;
using Humans.Surveys.Tests.Infrastructure;
using Humans.Users.Contracts;

namespace Humans.Surveys.Tests.Services;

/// <summary>
/// The per-template routing policy Surveys stamps on its own messages — template name and
/// opt-out category — the author-copy rendering the invitation allows, and the
/// gallery-coverage gate: every template <see cref="SurveysEmails"/> can build has a sample
/// in <see cref="SurveysEmailPreviews"/>, so a new template cannot ship invisible
/// (peterdrier/Humans#1651). Transport (opt-out, unsubscribe, wrapping, enqueue) stays
/// Email's, covered there.
/// </summary>
public sealed class SurveysEmailsTests
{
    private static SurveysEmails Create() => TestSurveysEmails.Create();

    [HumansFact]
    public void SurveyMail_IsSystemCategory()
    {
        var emails = Create();

        var invitation = emails.SurveyInvitation("a@x.com", "Alice", "Availability", "token", "en");
        invitation.RecipientEmail.Should().Be("a@x.com");
        invitation.TemplateName.Should().Be("survey_invitation");
        invitation.Category.Should().Be(MessageCategory.System);
        invitation.ReplyTo.Should().BeNull();

        var reminder = emails.SurveyReminder("a@x.com", "Alice", "Availability", "token", "en");
        reminder.TemplateName.Should().Be("survey_reminder");
        reminder.Category.Should().Be(MessageCategory.System);
    }

    [HumansFact]
    public void SurveyInvitation_custom_copy_renders_sanitized_markdown_with_https_images()
    {
        var msg = Create().SurveyInvitation(
            "a@x.com",
            "Daniel <Admin>",
            "Dates & places",
            "token + value",
            "en",
            "  Help choose our dates  ",
            "  **Choose carefully.**\r\n\r\n- Friday\r\n- Saturday\r\n\r\n[Details](https://example.com)\r\n\r\n![Poster](https://example.com/poster.png)\r\n<script>alert('x')</script>  ");

        msg.Subject.Should().Be("Help choose our dates");
        msg.HtmlBody.Should().Contain("Daniel &lt;Admin&gt;");
        msg.HtmlBody.Should().Contain("<h2>Dates &amp; places</h2>");
        msg.HtmlBody.Should().Contain("<p><strong>Choose carefully.</strong></p>");
        msg.HtmlBody.Should().Contain("<ul>");
        msg.HtmlBody.Should().Contain("<li>Friday</li>");
        msg.HtmlBody.Should().Contain("<a href=\"https://example.com\">Details</a>");
        msg.HtmlBody.Should().NotContain("<script>");
        msg.HtmlBody.Should().Contain("<img src=\"https://example.com/poster.png\"");
        msg.HtmlBody.Should().Contain(
            "https://humans.example/Survey/Answer?t=token%20%2B%20value");
    }

    [HumansFact]
    public void SurveyInvitation_blank_custom_copy_retains_standard_localized_wording()
    {
        var msg = Create().SurveyInvitation("a@x.com", "Daniel", "Test Survey", "token", "en", " ", null);

        msg.Subject.Should().Be("Please complete: Test Survey");
        msg.HtmlBody.Should().Contain(
            "<p>You're invited to complete <strong>Test Survey</strong>.</p>");
    }

    [HumansFact]
    public void SurveyReminder_LinksTheAnsweringWizardAbsolutely()
    {
        var msg = Create().SurveyReminder("a@x.com", "Alice", "Availability", "token + value", "en");

        msg.HtmlBody.Should().Contain("https://humans.example/Survey/Answer?t=token%20%2B%20value");
    }

    [HumansFact]
    public void RendersInTheRecipientsCulture()
    {
        Create().SurveyReminder("a@x.com", "Alice", "Availability", "token", "es")
            .Subject.Should().Be("Reminder: Availability");
    }

    [HumansFact]
    public void EveryTemplateHasAPreviewSample()
    {
        var emails = Create();
        var templates = TemplateNames(emails);
        templates.Should().NotBeEmpty();

        var sampled = new SurveysEmailPreviews(emails)
            .Samples(new EmailPreviewPersona("en", "Sally Smith", "sally@example.com"))
            .Select(s => s.Message.TemplateName);

        // survey_invitation samples twice (standard wording, custom copy), so compare
        // distinct sets.
        sampled.Distinct(StringComparer.Ordinal).Should().BeEquivalentTo(templates.Distinct(StringComparer.Ordinal),
            "every Surveys template needs a row in /Email/EmailPreview");
    }

    [HumansFact]
    public void PreviewSampleIdsAreUnique()
    {
        var samples = new SurveysEmailPreviews(Create())
            .Samples(new EmailPreviewPersona("en", "Sally Smith", "sally@example.com"));

        samples.Select(s => s.Id).Should().OnlyHaveUniqueItems();
    }

    /// <summary>
    /// Every template the builder can produce, found by reflection rather than listed, so a
    /// template added without a gallery sample fails <see cref="EveryTemplateHasAPreviewSample"/>.
    /// </summary>
    private static IReadOnlyList<string> TemplateNames(SurveysEmails emails) =>
    [
        .. typeof(SurveysEmails)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.ReturnType == typeof(EmailMessage))
            .Select(m => ((EmailMessage)m.Invoke(emails, [.. m.GetParameters().Select(SampleArgument)])!).TemplateName)
    ];

    private static object? SampleArgument(ParameterInfo parameter)
    {
        var type = parameter.ParameterType;
        // Optional custom copy stays null so the invitation samples its standard wording.
        if (parameter.IsOptional && !string.Equals(parameter.Name, "culture", StringComparison.Ordinal)) return null;
        if (type == typeof(string)) return string.Equals(parameter.Name, "culture", StringComparison.Ordinal) ? "en" : "sample";
        throw new NotSupportedException(
            $"SurveysEmails.{parameter.Member.Name} takes a {type.Name}; teach this test how to sample one.");
    }
}
