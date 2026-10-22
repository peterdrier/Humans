using Humans.Email.Contracts;
using NodaTime;

namespace Humans.Finance.Services;

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
            new EmailPreviewSample("sepa-payout-withdrawn", "SEPA Payout Withdrawn",
                emails.SepaPayoutWithdrawn(email, name, 123.45m, "ES79****789", new LocalDate(2026, 10, 18), culture)),
        ];
    }
}
