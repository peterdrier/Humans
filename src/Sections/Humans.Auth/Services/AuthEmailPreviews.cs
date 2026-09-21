using Humans.Email.Contracts;

namespace Humans.Auth.Services;

/// <summary>
/// Auth's contribution to the template gallery at <c>/Email/EmailPreview</c>: one sample
/// per template <see cref="AuthEmails"/> can send, built through the same builder the real
/// send path uses, so the gallery cannot drift from what members receive
/// (peterdrier/Humans#1651). Sample data only — no recipient is real, no link works and
/// nothing is sent.
/// </summary>
internal sealed class AuthEmailPreviews(AuthEmails emails) : IEmailPreviewContributor
{
    private const string SampleLoginUrl = "https://humans.example/Account/MagicLinkConfirm?userId=sample&token=sample";
    private const string SampleSignupUrl = "https://humans.example/Account/MagicLinkSignup?email=sample&token=sample";

    public IReadOnlyList<EmailPreviewSample> Samples(EmailPreviewPersona persona)
    {
        ArgumentNullException.ThrowIfNull(persona);
        var (culture, name, email) = (persona.Culture, persona.Name, persona.Email);

        return
        [
            new EmailPreviewSample("magic-link-login", "Magic Link Login",
                emails.MagicLinkLogin(email, name, SampleLoginUrl, culture)),
            new EmailPreviewSample("magic-link-signup", "Magic Link Signup",
                emails.MagicLinkSignup(email, SampleSignupUrl, culture)),
        ];
    }
}
