using Humans.Shifts.Domain;
using NodaTime;
using Humans.Base.Interfaces.Repositories;

namespace Humans.Shifts.Data;

internal interface IVolunteerTrackingRepository : IRepository
{
    /// <summary>Null event selects all events; user ids still restrict the result when supplied.</summary>
    Task<IReadOnlyList<VolunteerBuildStatus>> GetBuildStatusesForEventAsync(
        Guid? eventSettingsId,
        IReadOnlyCollection<Guid>? userIds = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<GeneralAvailability>> GetAvailabilityForEventAsync(
        Guid eventSettingsId,
        IReadOnlyCollection<Guid>? userIds = null,
        CancellationToken ct = default);

    /// <summary>GDPR Art. 17: removes the user's availability and build-status rows.</summary>
    Task<int> DeleteVolunteerTrackingForUserAsync(Guid userId, CancellationToken ct = default);

    Task<IReadOnlyList<GeneralAvailability>> GetAvailabilityForUserAsync(
        Guid userId,
        Guid? eventSettingsId = null,
        CancellationToken ct = default);

    Task UpsertAvailabilityAsync(
        Guid userId,
        Guid eventSettingsId,
        IReadOnlyList<int> dayOffsets,
        Instant now,
        CancellationToken ct = default);

    /// <summary>Account-merge fold.</summary>
    Task ReassignAvailabilityToUserAsync(
        Guid sourceUserId,
        Guid targetUserId,
        Instant updatedAt,
        CancellationToken ct = default);

    /// <summary>
    /// Upserts the camp set-up fields and, in the same save, removes <c>DayOffs</c> at or past
    /// <paramref name="setupOffsetThreshold"/> (null skips the trim). Returns the trimmed offsets, ascending.
    /// </summary>
    Task<IReadOnlyList<int>> UpsertCampSetupAsync(
        Guid userId,
        Guid eventSettingsId,
        LocalDate? barrioSetupStartDate,
        string? notes,
        Guid? setByUserId,
        Instant? setAt,
        int? setupOffsetThreshold,
        CancellationToken ct = default);

    /// <summary>At most one entry per day; creates the row if absent and keeps the list sorted.</summary>
    Task UpsertDayOffAsync(
        Guid userId,
        Guid eventSettingsId,
        DayOffEntry entry,
        CancellationToken ct = default);

    /// <summary>False when there is no row or no entry for the offset.</summary>
    Task<bool> RemoveDayOffAsync(
        Guid userId,
        Guid eventSettingsId,
        int dayOffset,
        CancellationToken ct = default);

}
