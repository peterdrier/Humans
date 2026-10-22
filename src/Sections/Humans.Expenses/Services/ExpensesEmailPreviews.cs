using Humans.Email.Contracts;

namespace Humans.Expenses.Services;

internal sealed class ExpensesEmailPreviews(ExpensesEmails emails) : IEmailPreviewContributor
{
    private static readonly Guid SampleReportId = new("0f1e2d3c-4b5a-4978-8695-a4b3c2d1e0f9");

    public IReadOnlyList<EmailPreviewSample> Samples(EmailPreviewPersona persona)
    {
        ArgumentNullException.ThrowIfNull(persona);
        var (culture, name, email) = (persona.Culture, persona.Name, persona.Email);

        return
        [
            new EmailPreviewSample("expense-approved", "Expense Report Approved",
                emails.ReportApproved(email, name, SampleReportId, 123.45m, "ES79****789", culture)),
        ];
    }
}
