using Humans.Email.Contracts;

namespace Humans.Consent.Services;

internal sealed class ConsentEmailPreviews(ConsentEmails emails) : IEmailPreviewContributor
{
    private static readonly string[] SampleDocs = ["Volunteer Agreement", "Privacy Policy"];

    public IReadOnlyList<EmailPreviewSample> Samples(EmailPreviewPersona persona)
    {
        ArgumentNullException.ThrowIfNull(persona);
        var (culture, name, email) = (persona.Culture, persona.Name, persona.Email);

        return
        [
            // One doc and many, because the subject line branches on the count.
            new EmailPreviewSample("reconsent-required", "Re-Consent Required (single doc)",
                emails.ReConsentsRequired(email, name, [SampleDocs[0]], culture)),
            new EmailPreviewSample("reconsents-required", "Re-Consents Required (multiple docs)",
                emails.ReConsentsRequired(email, name, SampleDocs, culture)),
            new EmailPreviewSample("reconsent-reminder", "Re-Consent Reminder",
                emails.ReConsentReminder(email, name, SampleDocs, 14, culture)),
        ];
    }
}
