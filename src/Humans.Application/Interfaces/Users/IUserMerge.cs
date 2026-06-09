using NodaTime;

namespace Humans.Application.Interfaces.Users;

/// <summary>
/// Sections implement this to participate in account merge. Each impl re-FKs
/// its user-keyed rows from the eliminated user (<paramref name="mergedFromUserId"/>
/// — the source row being tombstoned) onto the surviving user
/// (<paramref name="mergedToUserId"/> — the target).
///
/// <para>
/// The orchestrator (<c>AccountMergeService.MergeAsync</c>) fans out across
/// every registered implementation as ordered, independently-committing steps —
/// there is NO wrapping <c>TransactionScope</c>. Implementations MUST therefore
/// be idempotent: re-running "move source's rows to target" after a partial
/// failure must be a safe no-op (a re-FK of an already-moved row must not error).
/// Defer cache invalidation to the orchestrator, which evicts in a <c>finally</c>
/// after all steps complete.
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
