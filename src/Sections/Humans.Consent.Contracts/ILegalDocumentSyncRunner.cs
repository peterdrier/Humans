using Humans.Application.Interfaces;

namespace Humans.Consent.Contracts;

/// <summary>
/// What <c>SyncLegalDocumentsJob</c> actually does, expressed as one call.
/// </summary>
/// <remarks>
/// The job used to run the whole pass from <c>Humans.Infrastructure</c>: pull from GitHub,
/// fan out over the affected teams' members, filter to the ones missing the new versions,
/// and mail each of them. It named <c>LegalDocument</c> and <c>IConsentRepository</c>
/// directly to do it, and both turn internal at the move — so there is no version of this
/// that compiles as "give me the rows". The contract is "do the thing" (design §15 step 6b,
/// Email's rule); what stays in Base is the try/catch and <c>RecordJobRun</c>.
/// </remarks>
public interface ILegalDocumentSyncRunner : IApplicationService
{
    /// <summary>
    /// Syncs every legal document from GitHub and mails re-consent notices to the members
    /// of affected teams who have not signed the new versions. Logs its own progress; the
    /// caller owns only the job-level success/failure metric.
    /// </summary>
    Task SyncAndNotifyAsync(CancellationToken cancellationToken = default);
}
