using Humans.Base.Interfaces.Repositories;
using Humans.MailerLite.Domain;

namespace Humans.MailerLite.Data;

internal interface IMailerLiteRepository : IRepository
{
    /// <summary>One row per audience plus the import's; match by key in memory.</summary>
    Task<IReadOnlyList<MailerLiteSyncState>> GetSyncStatesAsync(CancellationToken ct = default);

    /// <summary>Null when that key has never synced.</summary>
    Task<MailerLiteSyncState?> GetSyncStateAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Inserts when the key has never synced. The row's <c>Id</c> survives every run (the audit entry points at
    /// it), so <paramref name="state"/>'s own <c>Id</c> is ignored.
    /// </summary>
    Task<MailerLiteSyncState> UpsertSyncStateAsync(
        MailerLiteSyncState state, CancellationToken ct = default);
}
