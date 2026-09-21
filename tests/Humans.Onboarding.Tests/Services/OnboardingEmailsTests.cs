using System.Reflection;
using AwesomeAssertions;
using Humans.Email.Contracts;
using Humans.Users.Contracts;
using Humans.Onboarding.Services;
using Humans.Onboarding.Tests.Infrastructure;

namespace Humans.Onboarding.Tests.Services;

/// <summary>
/// The per-template routing policy Onboarding stamps on its own messages — template name
/// and opt-out category — plus the gallery-coverage gate: every template
/// <see cref="OnboardingEmails"/> can build has a sample in
/// <see cref="OnboardingEmailPreviews"/>, so a new template cannot ship invisible
/// (peterdrier/Humans#1651). Transport (opt-out, unsubscribe, wrapping, enqueue) stays
/// Email's, covered there.
/// </summary>
public sealed class OnboardingEmailsTests
{
    private static OnboardingEmails Create() => TestOnboardingEmails.Create();

    [HumansFact]
    public void SignupRejected_StampsSystem()
    {
        var msg = Create().SignupRejected("a@x.com", "Alice", "nope", "en");

        msg.RecipientEmail.Should().Be("a@x.com");
        msg.RecipientName.Should().Be("Alice");
        msg.TemplateName.Should().Be("signup_rejected");
        msg.Category.Should().Be(MessageCategory.System);
        msg.ReplyTo.Should().BeNull();
    }

    [HumansFact]
    public void SignupRejected_OmitsTheReasonLineWhenThereIsNoReason()
    {
        var withReason = Create().SignupRejected("a@x.com", "Alice", "duplicate", "en");
        var without = Create().SignupRejected("a@x.com", "Alice", null, "en");

        withReason.HtmlBody.Should().Contain("<strong>Reason:</strong> duplicate");
        without.HtmlBody.Should().NotContain("Reason:");
    }

    [HumansFact]
    public void EveryTemplateHasAPreviewSample()
    {
        var emails = Create();
        var templates = TemplateNames(emails);
        templates.Should().NotBeEmpty();

        var sampled = new OnboardingEmailPreviews(emails)
            .Samples(new EmailPreviewPersona("en", "Sally Smith", "sally@example.com"))
            .Select(s => s.Message.TemplateName);

        sampled.Should().BeEquivalentTo(templates,
            "every Onboarding template needs a row in /Email/EmailPreview");
    }

    [HumansFact]
    public void PreviewSampleIdsAreUnique()
    {
        var samples = new OnboardingEmailPreviews(Create())
            .Samples(new EmailPreviewPersona("en", "Sally Smith", "sally@example.com"));

        samples.Select(s => s.Id).Should().OnlyHaveUniqueItems();
    }

    /// <summary>
    /// Every template the builder can produce, found by reflection rather than listed, so a
    /// template added without a gallery sample fails <see cref="EveryTemplateHasAPreviewSample"/>.
    /// </summary>
    private static IReadOnlyList<string> TemplateNames(OnboardingEmails emails) =>
    [
        .. typeof(OnboardingEmails)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.ReturnType == typeof(EmailMessage))
            .Select(m => ((EmailMessage)m.Invoke(emails, [.. m.GetParameters().Select(SampleArgument)])!).TemplateName)
    ];

    private static object SampleArgument(ParameterInfo parameter)
    {
        var type = parameter.ParameterType;
        if (type == typeof(string)) return string.Equals(parameter.Name, "culture", StringComparison.Ordinal) ? "en" : "sample";
        throw new NotSupportedException(
            $"OnboardingEmails.{parameter.Member.Name} takes a {type.Name}; teach this test how to sample one.");
    }
}
