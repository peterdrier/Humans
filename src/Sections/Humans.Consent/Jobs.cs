using Humans.Base.Interfaces;
using Humans.Consent.Jobs;

namespace Humans.Consent;

/// <summary>
/// Consent's recurring jobs. Discovered by Shell — nothing names it, so it needs no section
/// prefix. Named <c>SectionJobs</c> rather than the seam's own convention (<c>Jobs</c>)
/// because this section's jobs live under the <c>Humans.Consent.Jobs</c> namespace — a type
/// literally named <c>Jobs</c> here collides with it (CS0101) and can't implement a method
/// also named <c>Jobs</c> (CS0542).
/// </summary>
public sealed class SectionJobs : ISectionJobs
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
