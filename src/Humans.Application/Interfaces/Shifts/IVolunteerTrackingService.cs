using Humans.Application.DTOs;
using NodaTime;

namespace Humans.Application.Interfaces.Shifts;

public interface IVolunteerTrackingService
{
    Task<VolunteerTrackingViewModel> GetTrackingDataAsync(CancellationToken ct = default);

    /// <summary>Coordinator path. Caller has already authorized.</summary>
    Task<SetCampSetupResult> SetCampSetupAsync(
        Guid targetUserId, LocalDate barrioSetupStartDate,
        string? notes, Guid coordinatorUserId, CancellationToken ct = default);

    /// <summary>Coordinator path. Caller has already authorized.</summary>
    Task ClearCampSetupAsync(
        Guid targetUserId, Guid coordinatorUserId, CancellationToken ct = default);

    /// <summary>Coordinator path. Caller has already authorized.</summary>
    Task<SetBlockResult> SetBlockAsync(
        Guid targetUserId, int dayOffset, bool block,
        Guid coordinatorUserId, CancellationToken ct = default);

    /// <summary>
    /// Volunteer-self path. Caller MUST have set ownerUserId from
    /// ClaimsPrincipal — never from the form.
    /// </summary>
    Task<SaveOwnBlockedDaysResult> SaveOwnBlockedDaysAsync(
        Guid ownerUserId, IReadOnlyList<int> dayOffsets, CancellationToken ct = default);

    /// <summary>
    /// Read-side helper for /Shifts/Mine: returns the user's current blocked
    /// offsets plus the build-period bounds for rendering the calendar grid.
    /// Resolves "no active event" / "no row yet" itself.
    /// </summary>
    Task<MineBlockedDaysSummary> GetMineBlockedDaysSummaryAsync(
        Guid userId, CancellationToken ct = default);
}

public sealed record SetCampSetupResult(bool Ok, string? ErrorMessageKey);
public sealed record SetBlockResult(bool Ok, bool Changed, string? ErrorMessageKey);
public sealed record SaveOwnBlockedDaysResult(
    bool Ok, IReadOnlyList<int> Added, IReadOnlyList<int> Removed,
    IReadOnlyList<int> ResultingList, string? ErrorMessageKey);
public sealed record MineBlockedDaysSummary(
    bool HasActiveBuildPeriod,
    int BuildStartOffset,
    LocalDate GateOpeningDate,
    IReadOnlyList<int> BlockedDayOffsets);
