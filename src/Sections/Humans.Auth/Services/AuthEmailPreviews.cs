using Humans.Email.Contracts;

namespace Humans.Auth.Services;

internal sealed class AuthEmailPreviews(AuthEmails emails) : IEmailPreviewContributor
{
    private const string SampleSignInUrl = "https://humans.example/Account/SignInLinkConfirm?userId=sample&token=sample";
    private const string SampleSignupUrl = "https://humans.example/Account/SignInLinkSignup?email=sample&token=sample";

    public IReadOnlyList<EmailPreviewSample> Samples(EmailPreviewPersona persona)
    {
        ArgumentNullException.ThrowIfNull(persona);
        var (culture, name, email) = (persona.Culture, persona.Name, persona.Email);

        return
        [
            new EmailPreviewSample("sign-in-link", "Sign-in Link",
                emails.SignInLink(Guid.Empty, email, name, SampleSignInUrl, culture)),
            new EmailPreviewSample("sign-in-link-signup", "Sign-in Link Signup",
                emails.SignInLinkSignup(email, SampleSignupUrl, culture)),
        ];
    }
}
