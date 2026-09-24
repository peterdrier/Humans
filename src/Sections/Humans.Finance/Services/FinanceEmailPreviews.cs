using Humans.Email.Contracts;

namespace Humans.Finance.Services;

/// <summary>
/// Finance's contribution to the template gallery at <c>/Email/EmailPreview</c>: one
/// sample per template <see cref="FinanceEmails"/> can send, built through the same
/// builder the real send path uses, so the gallery cannot drift from what members receive
/// (peterdrier/Humans#1651). Sample data only — no recipient is real and nothing is sent.
/// </summary>
internal sealed class FinanceEmailPreviews(FinanceEmails emails) : IEmailPreviewContributor
{
    public IReadOnlyList<EmailPreviewSample> Samples(EmailPreviewPersona persona)
    {
        ArgumentNullException.ThrowIfNull(persona);
        var (culture, name, email) = (persona.Culture, persona.Name, persona.Email);

        return
        [
            new EmailPreviewSample("sepa-payout-generated", "SEPA Payout Sent",
                emails.SepaPayoutGenerated(email, name, 123.45m, "ES79****789", culture)),
        ];
    }
}
