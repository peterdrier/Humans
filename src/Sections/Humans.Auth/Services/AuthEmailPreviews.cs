using Humans.Email.Contracts;

namespace Humans.Auth.Services;

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
