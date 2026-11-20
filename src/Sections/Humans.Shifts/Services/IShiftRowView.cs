using Humans.Shifts.Services.Dtos;

namespace Humans.Shifts.Services;

/// <summary>
/// The per-user view for the section's own readers. <see cref="Humans.Shifts.Contracts.IShiftView"/> projects flat
/// summaries off the same reads.
/// </summary>
internal interface IShiftRowView
{
    /// <summary>Empty, never null, for an unknown user or no active event.</summary>
    ValueTask<ShiftUserView> GetUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Unknown users yield an empty entry.</summary>
    ValueTask<IReadOnlyDictionary<Guid, ShiftUserView>> GetUsersAsync(
        IEnumerable<Guid> userIds, CancellationToken ct = default);

}
