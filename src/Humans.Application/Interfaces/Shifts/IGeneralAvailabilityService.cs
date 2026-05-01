using Humans.Domain.Entities;
using NodaTime;

namespace Humans.Application.Interfaces.Shifts;

public interface IGeneralAvailabilityService
{
    Task SetAvailabilityAsync(Guid userId, Guid eventSettingsId, List<int> dayOffsets);
    Task<GeneralAvailability?> GetByUserAsync(Guid userId, Guid eventSettingsId);
    Task<List<GeneralAvailability>> GetAvailableForDayAsync(Guid eventSettingsId, int dayOffset);
    Task DeleteAsync(Guid userId, Guid eventSettingsId);

    /// <summary>
    /// Account-merge fold: bulk-moves <c>GeneralAvailability</c> rows from
    /// <paramref name="sourceUserId"/> to <paramref name="targetUserId"/>.
    /// Conflict on the unique <c>(UserId, EventSettingsId)</c> key is resolved
    /// target-wins: when both source and target have a row for the same
    /// event, the source row is dropped (target's availability stands).
    /// Otherwise the source row is re-FK'd to target with
    /// <paramref name="updatedAt"/> stamped on <c>UpdatedAt</c>. Returns the
    /// count of <c>GeneralAvailability</c> rows attributed to
    /// <paramref name="targetUserId"/> after the move. Called only by
    /// <c>AccountMergeService.AcceptAsync</c>.
    /// </summary>
    Task<int> ReassignToUserAsync(
        Guid sourceUserId,
        Guid targetUserId,
        Instant updatedAt,
        CancellationToken ct = default);
}
