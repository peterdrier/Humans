namespace Humans.Shifts.Contracts;

/// <summary>
/// ValueTask because the Singleton decorator completes synchronously on a cache hit. Missing ids, or no active
/// event, give an empty summary, never null or an exception.
/// </summary>
public interface IShiftView
{
    ValueTask<ShiftUserSummary> GetUserAsync(Guid userId, CancellationToken ct = default);

    ValueTask<IReadOnlyDictionary<Guid, ShiftUserSummary>> GetUsersAsync(
        IEnumerable<Guid> userIds, CancellationToken ct = default);
}
