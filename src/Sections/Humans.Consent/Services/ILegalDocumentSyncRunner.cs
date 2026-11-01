using Humans.Base.Interfaces;

namespace Humans.Consent.Services;

internal interface ILegalDocumentSyncRunner : IApplicationService
{
    /// <summary>
    /// Syncs every document from GitHub and mails re-consent notices to affected members who haven't signed. Logs
    /// its own progress; the caller owns only the job metric.
    /// </summary>
    Task SyncAndNotifyAsync(CancellationToken cancellationToken = default);
}
