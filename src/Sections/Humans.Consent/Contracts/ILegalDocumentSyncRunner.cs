using Humans.Base.Interfaces;

namespace Humans.Consent.Contracts;

/// <summary>
/// "Do the thing", not "give me the rows": the pass names types internal to this section, so no read-shaped
/// version compiles.
/// </summary>
public interface ILegalDocumentSyncRunner : IApplicationService
{
    /// <summary>
    /// Syncs every document from GitHub and mails re-consent notices to affected members who haven't signed. Logs
    /// its own progress; the caller owns only the job metric.
    /// </summary>
    Task SyncAndNotifyAsync(CancellationToken cancellationToken = default);
}
