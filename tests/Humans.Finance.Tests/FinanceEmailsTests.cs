using System.Reflection;
using AwesomeAssertions;
using Humans.Email.Contracts;
using Humans.Finance.Services;
using Humans.Users.Contracts;

namespace Humans.Finance.Tests;

/// <summary>
/// The per-template routing policy Finance stamps on its own messages — template name
/// and opt-out category — plus the gallery-coverage gate: every template
/// <see cref="FinanceEmails"/> can build has a sample in
/// <see cref="FinanceEmailPreviews"/>, so a new template cannot ship invisible
/// (peterdrier/Humans#1651). Transport (opt-out, unsubscribe, wrapping, enqueue) stays
/// Email's, covered there.
/// </summary>
public sealed class FinanceEmailsTests
{
    private static FinanceEmails Create() => TestFinanceEmails.Create();

    [HumansFact]
    public void SepaPayoutGenerated_StampsSystem_AndFormatsTheAmountForTheRecipient()
    {
        var msg = Create().SepaPayoutGenerated("a@x.com", "Alice <A>", 1234.5m, "ES79****789", "de");

        msg.RecipientEmail.Should().Be("a@x.com");
        msg.RecipientName.Should().Be("Alice <A>");
        msg.TemplateName.Should().Be("sepa_payout_generated");
        msg.Category.Should().Be(MessageCategory.System);
        msg.ReplyTo.Should().BeNull();
        msg.Subject.Should().EndWith("#de");
        msg.HtmlBody.Should().Contain("Alice &lt;A&gt;");
        msg.HtmlBody.Should().Contain("1.234,50 €");
        msg.HtmlBody.Should().Contain("ES79****789");
    }

    [HumansFact]
    public void EveryTemplateHasAPreviewSample()
    {
        var emails = Create();
        var templates = TemplateNames(emails);
        templates.Should().NotBeEmpty();

        var sampled = new FinanceEmailPreviews(emails)
            .Samples(new EmailPreviewPersona("en", "Sally Smith", "sally@example.com"))
            .Select(s => s.Message.TemplateName);

        sampled.Should().BeEquivalentTo(templates,
            "every Finance template needs a row in /Email/EmailPreview");
    }

    [HumansFact]
    public void PreviewSampleIdsAreUnique()
    {
        var samples = new FinanceEmailPreviews(Create())
            .Samples(new EmailPreviewPersona("en", "Sally Smith", "sally@example.com"));

        samples.Select(s => s.Id).Should().OnlyHaveUniqueItems();
    }

    /// <summary>
    /// Every template the builder can produce, found by reflection rather than listed, so a
    /// template added without a gallery sample fails <see cref="EveryTemplateHasAPreviewSample"/>.
    /// </summary>
    private static IReadOnlyList<string> TemplateNames(FinanceEmails emails) =>
    [
        .. typeof(FinanceEmails)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.ReturnType == typeof(EmailMessage))
            .Select(m => ((EmailMessage)m.Invoke(emails, [.. m.GetParameters().Select(SampleArgument)])!).TemplateName)
    ];

    private static object SampleArgument(ParameterInfo parameter)
    {
        var type = parameter.ParameterType;
        if (type == typeof(string)) return string.Equals(parameter.Name, "culture", StringComparison.Ordinal) ? "en" : "sample";
        if (type == typeof(decimal)) return 1m;
        throw new NotSupportedException(
            $"FinanceEmails.{parameter.Member.Name} takes a {type.Name}; teach this test how to sample one.");
    }
}
