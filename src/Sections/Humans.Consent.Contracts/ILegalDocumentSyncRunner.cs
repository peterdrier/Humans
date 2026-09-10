using Humans.Base.Interfaces;

namespace Humans.Consent.Contracts;

/// <summary>
/// What <c>SyncLegalDocumentsJob</c> actually does, expressed as one call.
/// </summary>
/// <remarks>
/// The contract is "do the thing", not "give me the rows": the pass names
/// <c>LegalDocument</c> and <c>IConsentRepository</c>, both internal here, so no read-shaped
/// version of it compiles (design §15 step 6b, Email's rule). The job keeps only the
/// try/catch and <c>RecordJobRun</c>. Its one consumer, <c>SyncLegalDocumentsJob</c>, is now
/// in-section; folding the interface inward shrinks contract surface and needs Peter.
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
