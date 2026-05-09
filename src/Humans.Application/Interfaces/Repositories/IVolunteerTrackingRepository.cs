using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Interfaces.Repositories;

/// <summary>
/// Shifts-owned I/O for the new volunteer_build_statuses table plus the
/// scoped Build-period signup read used by the gap detector. All methods
/// return materialized lists / nullable rows — no IQueryable leaks.
/// </summary>
public interface IVolunteerTrackingRepository
{
    /// <summary>Fetch the row for (userId, eventSettingsId), or null.</summary>
    Task<VolunteerBuildStatus?> GetAsync(
        Guid userId, Guid eventSettingsId, CancellationToken ct = default);

    /// <summary>All rows for the event keyed by UserId. Empty list if none.</summary>
    Task<IReadOnlyList<VolunteerBuildStatus>> GetByEventAsync(
        Guid eventSettingsId, CancellationToken ct = default);

    /// <summary>
    /// Upsert (UserId, EventSettingsId): mutate or insert the row's camp set-up
    /// fields. The caller has already validated barrioSetupStartDate (or null
    /// to clear).
    /// </summary>
    Task<VolunteerBuildStatus> UpsertCampSetupAsync(
        Guid userId,
        Guid eventSettingsId,
        LocalDate? barrioSetupStartDate,
        string? notes,
        Guid? setByUserId,
        Instant? setAt,
        CancellationToken ct = default);

    /// <summary>
    /// All eligible Build-period signups for the event: rows where
    /// Shift.DayOffset ∈ [BuildStartOffset, 0), the rota's period
    /// is Build or All, and Status ∈ {Confirmed, Pending}.
    /// </summary>
    Task<IReadOnlyList<EligibleBuildSignup>> GetEligibleBuildSignupsAsync(
        Guid eventSettingsId, CancellationToken ct = default);
}

/// <summary>
/// Projection: just what the gap-detector needs for a single eligible signup.
/// RotaName is the parent rota's display name, used by the heatmap partial
/// to populate cell-click popovers.
/// </summary>
public sealed record EligibleBuildSignup(
    Guid UserId,
    int DayOffset,
    SignupStatus Status,
    string RotaName);
