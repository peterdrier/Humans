using System.Reflection;
using AwesomeAssertions;
using Humans.Consent.Services;
using Humans.Consent.Tests.Infrastructure;
using Humans.Email.Contracts;

namespace Humans.Consent.Tests.Services;

/// <summary>
/// The per-template routing policy Consent stamps on its own messages — template name and
/// opt-out category — plus the gallery-coverage gate: every template
/// <see cref="ConsentEmails"/> can build has a sample in <see cref="ConsentEmailPreviews"/>,
/// so a new template cannot ship invisible (peterdrier/Humans#1651). Transport (opt-out,
/// unsubscribe, wrapping, enqueue) stays Email's, covered there.
/// </summary>
public sealed class ConsentEmailsTests
{
    private static ConsentEmails Create() => TestConsentEmails.Create();

    [HumansFact]
    public void ReConsentsRequired_IsAlwaysSend_NullCategory()
    {
        var msg = Create().ReConsentsRequired("a@x.com", "Alice", ["Doc"], "en");

        msg.RecipientEmail.Should().Be("a@x.com");
        msg.TemplateName.Should().Be("reconsents_required");
        msg.Category.Should().BeNull();
        msg.ReplyTo.Should().BeNull();
    }

    [HumansFact]
    public void ReConsentReminder_IsAlwaysSend_NullCategory()
    {
        var msg = Create().ReConsentReminder("a@x.com", "Alice", ["Doc"], 14, "en");

        msg.TemplateName.Should().Be("reconsent_reminder");
        msg.Category.Should().BeNull();
    }

    [HumansFact]
    public void ReConsentsRequired_SubjectNamesTheDocumentOnlyWhenThereIsOne()
    {
        var single = Create().ReConsentsRequired("a@x.com", "Alice", ["Volunteer Agreement"], "en");
        single.Subject.Should().Be("Please re-accept: Volunteer Agreement");

        var many = Create().ReConsentsRequired("a@x.com", "Alice", ["Volunteer Agreement", "Privacy Policy"], "en");
        many.Subject.Should().Be("Consent_Email_ReConsentRequired_Subject_Multiple#en");
    }

    [HumansFact]
    public void DocumentNamesAreHtmlEncodedIntoTheList()
    {
        var msg = Create().ReConsentsRequired("a@x.com", "Alice", ["<b>Doc</b>"], "en");

        msg.HtmlBody.Should().Contain("<li><strong>&lt;b&gt;Doc&lt;/b&gt;</strong></li>");
    }

    [HumansFact]
    public void RendersInTheRecipientsCulture()
    {
        Create().ReConsentsRequired("a@x.com", "Alice", ["A", "B"], "es")
            .Subject.Should().Be("Consent_Email_ReConsentRequired_Subject_Multiple#es");
    }

    [HumansFact]
    public void EveryTemplateHasAPreviewSample()
    {
        var emails = Create();
        var templates = TemplateNames(emails);
        templates.Should().NotBeEmpty();

        var sampled = new ConsentEmailPreviews(emails)
            .Samples(new EmailPreviewPersona("en", "Sally Smith", "sally@example.com"))
            .Select(s => s.Message.TemplateName)
            .Distinct(StringComparer.Ordinal);

        // Distinct: the gallery shows reconsents_required twice on purpose, once per
        // branch of the count-dependent subject line.
        sampled.Should().BeEquivalentTo(templates,
            "every Consent template needs a row in /Email/EmailPreview");
    }

    [HumansFact]
    public void PreviewSampleIdsAreUnique()
    {
        var samples = new ConsentEmailPreviews(Create())
            .Samples(new EmailPreviewPersona("en", "Sally Smith", "sally@example.com"));

        samples.Select(s => s.Id).Should().OnlyHaveUniqueItems();
    }

    /// <summary>
    /// Every template the builder can produce, found by reflection rather than listed, so a
    /// template added without a gallery sample fails <see cref="EveryTemplateHasAPreviewSample"/>.
    /// </summary>
    private static IReadOnlyList<string> TemplateNames(ConsentEmails emails) =>
    [
        .. typeof(ConsentEmails)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.ReturnType == typeof(EmailMessage))
            .Select(m => ((EmailMessage)m.Invoke(emails, [.. m.GetParameters().Select(SampleArgument)])!).TemplateName)
    ];

    private static object SampleArgument(ParameterInfo parameter)
    {
        var type = parameter.ParameterType;
        if (type == typeof(string)) return string.Equals(parameter.Name, "culture", StringComparison.Ordinal) ? "en" : "sample";
        if (type == typeof(int)) return 14;
        if (type == typeof(IReadOnlyList<string>)) return new[] { "sample" };
        throw new NotSupportedException(
            $"ConsentEmails.{parameter.Member.Name} takes a {type.Name}; teach this test how to sample one.");
    }
}
