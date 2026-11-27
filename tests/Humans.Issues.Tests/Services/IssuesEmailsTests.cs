using System.Reflection;
using AwesomeAssertions;
using Humans.Email.Contracts;
using Humans.Issues.Services;
using Humans.Issues.Tests.Infrastructure;

namespace Humans.Issues.Tests.Services;

/// <summary>
/// Includes the gallery-coverage gate: every template <see cref="IssuesEmails"/> can build has a sample in <see cref="IssuesEmailPreviews"/>, so a new template cannot ship invisible.
/// </summary>
public sealed class IssuesEmailsTests
{
    private static IssuesEmails Create() => TestIssuesEmails.Create();

    [HumansFact]
    public void IssueComment_StampsSystem()
    {
        var msg = Create().IssueComment(Guid.NewGuid(), "a@x.com", "Alice", "Title", "Body", "/Issues/12", "en");

        msg.RecipientEmail.Should().Be("a@x.com");
        msg.RecipientName.Should().Be("Alice");
        msg.TemplateName.Should().Be("issue_comment");
        msg.Category.Should().Be(MessageCategory.System);
        msg.ReplyTo.Should().BeNull();
    }

    [HumansFact]
    public void IssueComment_Markdown_RendersSanitized()
    {
        var msg = Create().IssueComment(
            Guid.NewGuid(),
            "a@x.com",
            "Daniel <Admin>",
            "Lights & sound",
            "**Looking at it.**\r\n\r\n[Details](https://example.com)\r\n\r\n![Shot](https://example.com/shot.png)\r\n<script>alert('x')</script>",
            "/Issues/12",
            "en");

        msg.HtmlBody.Should().Contain("Daniel &lt;Admin&gt;");
        msg.HtmlBody.Should().Contain("Lights &amp; sound");
        msg.HtmlBody.Should().Contain("<p><strong>Looking at it.</strong></p>");
        msg.HtmlBody.Should().Contain("<a href=\"https://example.com\">Details</a>");
        msg.HtmlBody.Should().NotContain("<script>");
        msg.HtmlBody.Should().Contain("<img src=\"https://example.com/shot.png\"");
    }

    [HumansFact]
    public void IssueComment_Subject_IsNotHtmlEncoded()
    {
        var msg = Create().IssueComment(Guid.NewGuid(), "a@x.com", "Alice", "Lights & sound", "Body", "/Issues/12", "en");

        msg.Subject.Should().Be("New comment on Lights & sound");
        msg.HtmlBody.Should().Contain("Lights &amp; sound");
    }

    [HumansFact]
    public void IssueComment_RelativeIssueLink_BecomesAbsolute()
    {
        var relative = Create().IssueComment(Guid.NewGuid(), "a@x.com", "Alice", "Title", "Body", "/Issues/12", "en");
        var absolute = Create().IssueComment(Guid.NewGuid(), "a@x.com", "Alice", "Title", "Body", "https://elsewhere.example/Issues/12", "en");

        relative.HtmlBody.Should().Contain($"{TestIssuesEmails.BaseUrl}/Issues/12");
        absolute.HtmlBody.Should().Contain("https://elsewhere.example/Issues/12");
    }

    [HumansFact]
    public void Templates_EachHasPreviewSample()
    {
        var emails = Create();
        var templates = TemplateNames(emails);
        templates.Should().NotBeEmpty();

        var sampled = new IssuesEmailPreviews(emails)
            .Samples(new EmailPreviewPersona("en", "Sally Smith", "sally@example.com"))
            .Select(s => s.Message.TemplateName);

        sampled.Should().BeEquivalentTo(templates,
            "every Issues template needs a row in /Email/EmailPreview");
    }

    [HumansFact]
    public void PreviewSampleIds_AreUnique()
    {
        var samples = new IssuesEmailPreviews(Create())
            .Samples(new EmailPreviewPersona("en", "Sally Smith", "sally@example.com"));

        samples.Select(s => s.Id).Should().OnlyHaveUniqueItems();
    }

    /// <summary>
    /// Every template the builder can produce, found by reflection rather than listed, so a
    /// template added without a gallery sample fails <see cref="Templates_EachHasPreviewSample"/>.
    /// </summary>
    private static IReadOnlyList<string> TemplateNames(IssuesEmails emails) =>
    [
        .. typeof(IssuesEmails)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.ReturnType == typeof(EmailMessage))
            .Select(m => ((EmailMessage)m.Invoke(emails, [.. m.GetParameters().Select(SampleArgument)])!).TemplateName)
    ];

    private static object SampleArgument(ParameterInfo parameter)
    {
        var type = parameter.ParameterType;
        if (type == typeof(Guid)) return Guid.NewGuid();
        if (type == typeof(string)) return string.Equals(parameter.Name, "preferredLanguage", StringComparison.Ordinal) ? "en" : "sample";
        throw new NotSupportedException(
            $"IssuesEmails.{parameter.Member.Name} takes a {type.Name}; teach this test how to sample one.");
    }
}
