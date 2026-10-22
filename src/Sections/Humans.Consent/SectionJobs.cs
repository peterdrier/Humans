using Humans.Base.Interfaces;
using Humans.Consent.Jobs;

namespace Humans.Consent;

internal sealed class SectionJobs : ISectionJobs
{
    public IEnumerable<RecurringJobDescriptor> Jobs(IServiceProvider services)
    {
        // 04:00, 30 minutes before Users' SuspendNonCompliantMembersJob.
        yield return new RecurringJobDescriptor(
            "consent-reconsent-reminders", typeof(SendReConsentReminderJob), "0 4 * * *");

        yield return new RecurringJobDescriptor(
            "consent-legal-document-sync", typeof(SyncLegalDocumentsJob), "0 4 * * *");
    }
}
