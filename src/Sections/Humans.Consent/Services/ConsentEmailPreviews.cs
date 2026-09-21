using Humans.Email.Contracts;

namespace Humans.Consent.Services;

/// <summary>
/// Consent's contribution to the template gallery at <c>/Email/EmailPreview</c>: one
/// sample per template <see cref="ConsentEmails"/> can send, built through the same
/// builder the real send path uses, so the gallery cannot drift from what members receive
/// (peterdrier/Humans#1651). Sample data only — no recipient is real and nothing is sent.
/// </summary>
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
