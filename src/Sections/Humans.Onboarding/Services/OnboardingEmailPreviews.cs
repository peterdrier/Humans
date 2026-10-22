using Humans.Email.Contracts;

namespace Humans.Onboarding.Services;

internal sealed class OnboardingEmailPreviews(OnboardingEmails emails) : IEmailPreviewContributor
{
    private const string SampleReason = "Incomplete profile information";

    public IReadOnlyList<EmailPreviewSample> Samples(EmailPreviewPersona persona)
    {
        ArgumentNullException.ThrowIfNull(persona);
        var (culture, name, email) = (persona.Culture, persona.Name, persona.Email);

        return
        [
            new EmailPreviewSample("signup-rejected", "Signup Rejected",
                emails.SignupRejected(email, name, SampleReason, culture)),
        ];
    }
}
