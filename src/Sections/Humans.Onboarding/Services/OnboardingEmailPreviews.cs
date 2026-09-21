using Humans.Email.Contracts;

namespace Humans.Onboarding.Services;

/// <summary>
/// Onboarding's contribution to the template gallery at <c>/Email/EmailPreview</c>: one
/// sample per template <see cref="OnboardingEmails"/> can send, built through the same
/// builder the real send path uses, so the gallery cannot drift from what members receive
/// (peterdrier/Humans#1651). Sample data only — no recipient is real and nothing is sent.
/// </summary>
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
