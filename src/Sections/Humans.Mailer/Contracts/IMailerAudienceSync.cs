namespace Humans.Mailer.Contracts;

/// <summary>
/// The audience push <c>MailerAudienceSyncJob</c> drives. Everything the sync needs — the
/// registered audience list, the MailerLite diff, the per-audience counts and the summary
/// audit entry — lives inside the section; the job is the scheduler shim around it
/// (design §15 step 6b).
/// </summary>
/// <remarks>
/// Deliberately narrower than the section's own <c>IMailerAudienceSyncService</c>: the job
/// reads exactly one number off the result (how many audiences were processed) for its
/// completion log, so publishing <c>AudienceSyncResult</c> and the three stat members would
/// be surface nothing outside the section consumes (Notifications' rule — carve the leaf
/// from the call sites, not from the interface).
/// </remarks>
public interface IMailerAudienceSync
{
    /// <summary>
    /// Pushes every registered audience to MailerLite as the scheduled (actor-less) run.
    /// Returns the number of audiences that completed, so the job can keep logging it.
    /// </summary>
    Task<int> SyncAllAudiencesAsync(CancellationToken cancellationToken = default);
}
