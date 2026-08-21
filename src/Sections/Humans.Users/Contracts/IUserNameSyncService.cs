using Humans.Base.Interfaces;

namespace Humans.Users.Contracts;

/// <summary>
/// Backs the <c>/Profile/Admin/NameBackfill</c> screen (nobodies-collective/Humans#1097).
/// Reports which humans still carry their names only on <c>Profile</c>, and copies them onto
/// the <c>User</c> row on an operator click. Idempotent — re-running a completed sync is a no-op.
/// </summary>
public interface IUserNameSyncService : IApplicationService
{
    /// <summary>Humans whose <c>User</c> name columns are still blank while the Profile has a value.</summary>
    Task<IReadOnlyList<UnsyncedNameRow>> GetUnsyncedAsync(CancellationToken ct = default);

    /// <summary>
    /// Re-persists each unsynced Profile, which mirrors its names onto the owning User row.
    /// Returns the number of humans synced.
    /// </summary>
    Task<int> SyncAllAsync(CancellationToken ct = default);
}

/// <summary>One human whose User-side names have not been filled in yet.</summary>
public sealed record UnsyncedNameRow(
    Guid UserId,
    string Email,
    string ProfileBurnerName,
    string ProfileLegalName,
    bool BurnerNameMissing,
    bool LegalNameMissing);
