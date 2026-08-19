using Humans.Base.Interfaces;
using Humans.Consent.Jobs;

namespace Humans.Consent;

/// <summary>Consent's recurring jobs. Discovered by Shell — nothing names it, so it needs no section prefix.</summary>
internal sealed class SectionJobs : ISectionJobs
{
    public IEnumerable<RecurringJobDescriptor> Jobs(IServiceProvider services)
    {
        // Send re-consent reminders before the suspension job runs.
        // Runs daily at 04:00, 30 minutes before Users' SuspendNonCompliantMembersJob.
        yield return new RecurringJobDescriptor(
            "consent-reconsent-reminders", typeof(SendReConsentReminderJob), "0 4 * * *");

        yield return new RecurringJobDescriptor(
            "consent-legal-document-sync", typeof(SyncLegalDocumentsJob), "0 4 * * *");
    }
}
