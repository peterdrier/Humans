namespace Humans.Shifts.Contracts;

/// <summary>
/// Read-only Shifts-section view surface for consumers outside the section.
/// Returns a flat, immutable per-user projection
/// (<see cref="ShiftUserSummary"/>) keyed by user id.
/// </summary>
/// <remarks>
/// Methods return <see cref="ValueTask{TResult}"/>: the public registration is
/// a Singleton decorator (<c>CachingShiftViewService</c>) that completes
/// synchronously on dict hits (no <see cref="System.Threading.Tasks.Task"/>
/// allocation, no thread hop) and falls through to an awaiting load on miss.
/// Missing ids — or no active event — return an empty summary, never
/// <c>null</c>, never an exception.
///
/// <para>
/// Issue #720 introduced this surface over bundled EF rows. The rows
/// themselves are the section's own vocabulary and do not cross the boundary:
/// the entity-bearing bundle lives on the section's internal
/// <c>IShiftRowView</c>, and this interface carries the flattened projection
/// of it. The per-rota bundle has no consumer outside the section at all and
/// is only on the internal interface (nobodies-collective/Humans#866, G5).
/// </para>
/// </remarks>
public interface IShiftView
{
    /// <summary>
    /// Returns the summary for a single user. Never <c>null</c> — an empty
    /// summary for unknown users / no active event.
    /// </summary>
    ValueTask<ShiftUserSummary> GetUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns summaries for many users in one call, keyed by user id.
    /// Unknown users yield an empty summary entry.
    /// </summary>
    ValueTask<IReadOnlyDictionary<Guid, ShiftUserSummary>> GetUsersAsync(
        IEnumerable<Guid> userIds, CancellationToken ct = default);
}
