using System.Reflection;
using AwesomeAssertions;
using Humans.Auth.Services;
using Humans.Auth.Tests.Infrastructure;
using Humans.Email.Contracts;

namespace Humans.Auth.Tests.Services;

/// <summary>
/// The per-template routing policy Auth stamps on its own messages — template name and
/// opt-out category — plus the gallery-coverage gate: every template
/// <see cref="AuthEmails"/> can build has a sample in <see cref="AuthEmailPreviews"/>, so a
/// new template cannot ship invisible (peterdrier/Humans#1651). Transport (opt-out,
/// unsubscribe, wrapping, enqueue, the immediate drain these two names key) stays Email's,
/// covered there.
/// </summary>
public sealed class AuthEmailsTests
{
    private static AuthEmails Create() => TestAuthEmails.Create();

    [HumansFact]
    public void MagicLinkLogin_IsAlwaysSend_TimeSensitiveTemplate()
    {
        var msg = Create().MagicLinkLogin("a@x.com", "Alice", "https://link", "en");

        msg.RecipientEmail.Should().Be("a@x.com");
        msg.RecipientName.Should().Be("Alice");
        msg.TemplateName.Should().Be(TimeSensitiveTemplates.MagicLinkLogin);
        msg.Category.Should().BeNull();
        msg.ReplyTo.Should().BeNull();
        msg.HtmlBody.Should().Contain("https://link");
    }

    [HumansFact]
    public void MagicLinkSignup_UsesAddressAsName()
    {
        var msg = Create().MagicLinkSignup("new@x.com", "https://link", "en");

        msg.RecipientEmail.Should().Be("new@x.com");
        msg.RecipientName.Should().Be("new@x.com");
        msg.TemplateName.Should().Be(TimeSensitiveTemplates.MagicLinkSignup);
        msg.Category.Should().BeNull();
    }

    [HumansFact]
    public void MagicLinkLogin_HtmlEncodesTheDisplayName()
    {
        var msg = Create().MagicLinkLogin("a@x.com", "<b>Alice</b>", "https://link", "en");

        msg.HtmlBody.Should().Contain("&lt;b&gt;Alice&lt;/b&gt;");
    }

    [HumansFact]
    public void RendersInTheRecipientsCulture()
    {
        Create().MagicLinkLogin("a@x.com", "Alice", "https://link", "es")
            .Subject.Should().Be("Auth_Email_MagicLinkLogin_Subject#es");
    }

    [HumansFact]
    public void EveryTemplateHasAPreviewSample()
    {
        var emails = Create();
        var templates = TemplateNames(emails);
        templates.Should().NotBeEmpty();

        var sampled = new AuthEmailPreviews(emails)
            .Samples(new EmailPreviewPersona("en", "Sally Smith", "sally@example.com"))
            .Select(s => s.Message.TemplateName);

        sampled.Should().BeEquivalentTo(templates,
            "every Auth template needs a row in /Email/EmailPreview");
    }

    [HumansFact]
    public void PreviewSampleIdsAreUnique()
    {
        var samples = new AuthEmailPreviews(Create())
            .Samples(new EmailPreviewPersona("en", "Sally Smith", "sally@example.com"));

        samples.Select(s => s.Id).Should().OnlyHaveUniqueItems();
    }

    /// <summary>
    /// Every template the builder can produce, found by reflection rather than listed, so a
    /// template added without a gallery sample fails <see cref="EveryTemplateHasAPreviewSample"/>.
    /// </summary>
    private static IReadOnlyList<string> TemplateNames(AuthEmails emails) =>
    [
        .. typeof(AuthEmails)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.ReturnType == typeof(EmailMessage))
            .Select(m => ((EmailMessage)m.Invoke(emails, [.. m.GetParameters().Select(SampleArgument)])!).TemplateName)
    ];

    private static object SampleArgument(ParameterInfo parameter)
    {
        var type = parameter.ParameterType;
        if (type == typeof(string)) return string.Equals(parameter.Name, "culture", StringComparison.Ordinal) ? "en" : "sample";
        throw new NotSupportedException(
            $"AuthEmails.{parameter.Member.Name} takes a {type.Name}; teach this test how to sample one.");
    }
}
