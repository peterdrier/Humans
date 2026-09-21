using System.Reflection;
using AwesomeAssertions;
using Humans.Email.Contracts;
using Humans.Feedback.Services;
using Humans.Feedback.Tests.Infrastructure;
using Humans.Users.Contracts;

namespace Humans.Feedback.Tests.Services;

/// <summary>
/// The per-template routing policy Feedback stamps on its own messages — template name
/// and opt-out category — plus the gallery-coverage gate: every template
/// <see cref="FeedbackEmails"/> can build has a sample in
/// <see cref="FeedbackEmailPreviews"/>, so a new template cannot ship invisible
/// (peterdrier/Humans#1651). Transport (opt-out, unsubscribe, wrapping, enqueue) stays
/// Email's, covered there.
/// </summary>
public sealed class FeedbackEmailsTests
{
    private static FeedbackEmails Create() => TestFeedbackEmails.Create();

    [HumansFact]
    public void FeedbackResponse_StampsSystem()
    {
        var msg = Create().FeedbackResponse("a@x.com", "Alice", "It broke", "Fixed.", "/Feedback/12", "en");

        msg.RecipientEmail.Should().Be("a@x.com");
        msg.RecipientName.Should().Be("Alice");
        msg.TemplateName.Should().Be("feedback_response");
        msg.Category.Should().Be(MessageCategory.System);
        msg.ReplyTo.Should().BeNull();
    }

    [HumansFact]
    public void FeedbackResponse_renders_sanitized_markdown_with_https_images()
    {
        var msg = Create().FeedbackResponse(
            "a@x.com",
            "Daniel <Admin>",
            "The <b>lights</b> were off",
            "**Fixed.**\r\n\r\n[Details](https://example.com)\r\n\r\n![Shot](https://example.com/shot.png)\r\n<script>alert('x')</script>",
            "/Feedback/12",
            "en");

        msg.HtmlBody.Should().Contain("Daniel &lt;Admin&gt;");
        msg.HtmlBody.Should().Contain("The &lt;b&gt;lights&lt;/b&gt; were off");
        msg.HtmlBody.Should().Contain("<p><strong>Fixed.</strong></p>");
        msg.HtmlBody.Should().Contain("<a href=\"https://example.com\">Details</a>");
        msg.HtmlBody.Should().NotContain("<script>");
        msg.HtmlBody.Should().Contain("<img src=\"https://example.com/shot.png\"");
    }

    [HumansFact]
    public void EveryTemplateHasAPreviewSample()
    {
        var emails = Create();
        var templates = TemplateNames(emails);
        templates.Should().NotBeEmpty();

        var sampled = new FeedbackEmailPreviews(emails)
            .Samples(new EmailPreviewPersona("en", "Sally Smith", "sally@example.com"))
            .Select(s => s.Message.TemplateName);

        sampled.Should().BeEquivalentTo(templates,
            "every Feedback template needs a row in /Email/EmailPreview");
    }

    [HumansFact]
    public void PreviewSampleIdsAreUnique()
    {
        var samples = new FeedbackEmailPreviews(Create())
            .Samples(new EmailPreviewPersona("en", "Sally Smith", "sally@example.com"));

        samples.Select(s => s.Id).Should().OnlyHaveUniqueItems();
    }

    /// <summary>
    /// Every template the builder can produce, found by reflection rather than listed, so a
    /// template added without a gallery sample fails <see cref="EveryTemplateHasAPreviewSample"/>.
    /// </summary>
    private static IReadOnlyList<string> TemplateNames(FeedbackEmails emails) =>
    [
        .. typeof(FeedbackEmails)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.ReturnType == typeof(EmailMessage))
            .Select(m => ((EmailMessage)m.Invoke(emails, [.. m.GetParameters().Select(SampleArgument)])!).TemplateName)
    ];

    private static object SampleArgument(ParameterInfo parameter)
    {
        var type = parameter.ParameterType;
        if (type == typeof(string)) return string.Equals(parameter.Name, "culture", StringComparison.Ordinal) ? "en" : "sample";
        throw new NotSupportedException(
            $"FeedbackEmails.{parameter.Member.Name} takes a {type.Name}; teach this test how to sample one.");
    }
}
