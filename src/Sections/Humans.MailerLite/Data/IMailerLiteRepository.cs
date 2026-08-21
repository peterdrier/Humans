using Humans.Base.Interfaces.Repositories;
using Humans.MailerLite.Domain;

namespace Humans.MailerLite.Data;

/// <summary>The only type allowed to touch <c>mailerlite_sync_states</c>.</summary>
internal interface IMailerLiteRepository : IRepository
{
    /// <summary>Every sync state — nine rows at most, so the caller matches by key in memory.</summary>
    Task<IReadOnlyList<MailerLiteSyncState>> GetSyncStatesAsync(CancellationToken ct = default);

    /// <summary>One sync state, or null when that key has never synced.</summary>
    Task<MailerLiteSyncState?> GetSyncStateAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Writes <paramref name="state"/>'s values onto the row for its <c>Key</c>, inserting one
    /// if the key has never synced, and returns the persisted row. The row's <c>Id</c> survives
    /// every run — the sync's audit entry points at it — so <paramref name="state"/>'s own
    /// <c>Id</c> is ignored.
    /// </summary>
    Task<MailerLiteSyncState> UpsertSyncStateAsync(
        MailerLiteSyncState state, CancellationToken ct = default);
}
