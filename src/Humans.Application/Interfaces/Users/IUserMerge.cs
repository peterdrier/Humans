using NodaTime;

namespace Humans.Application.Interfaces.Users;

/// <summary>
/// Sections implement this to participate in account merge. Each impl re-FKs
/// its user-keyed rows from the eliminated user (<paramref name="mergedFromUserId"/>
/// — the source row being tombstoned) onto the surviving user
/// (<paramref name="mergedToUserId"/> — the target).
///
/// <para>
/// The orchestrator (<c>AccountMergeService.AcceptAsync</c>) fans out across
/// every registered implementation inside an ambient <c>TransactionScope</c>,
/// so all section moves either commit or roll back atomically. Impls may
/// invalidate their own caches inline if eviction (not synchronous DB rebuild)
/// is safe-on-rollback; otherwise the orchestrator handles cache refresh in
/// its post-commit block.
/// </para>
/// </summary>
public interface IUserMerge
{
    /// <summary>
    /// Re-FK this section's user-keyed rows from
    /// <paramref name="mergedFromUserId"/> (the eliminated/tombstoned user)
    /// onto <paramref name="mergedToUserId"/> (the surviving user).
    /// Implementations resolve same-row conflicts per their own section's
    /// rules and stamp <paramref name="now"/> as <c>UpdatedAt</c> on tables
    /// that carry one.
    /// </summary>
    Task ReassignAsync(Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now, CancellationToken ct);
}
