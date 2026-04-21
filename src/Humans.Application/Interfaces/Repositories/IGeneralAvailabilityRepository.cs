using Humans.Domain.Entities;

namespace Humans.Application.Interfaces.Repositories;

/// <summary>
/// Repository for the Shifts section's <c>general_availability</c> table.
/// The only non-test file that may write to or query
/// <c>DbContext.GeneralAvailability</c> from the <c>GeneralAvailabilityService</c>
/// migration onward.
/// </summary>
/// <remarks>
/// <para>
/// Entities-in / entities-out. Read methods are <c>AsNoTracking</c>. Uses
/// <see cref="Microsoft.EntityFrameworkCore.IDbContextFactory{TContext}"/>
/// so the repository can be registered as Singleton while
/// <c>HumansDbContext</c> remains Scoped.
/// </para>
/// <para>
/// Scope note: this is a narrow, Shifts-section repository — only the surface
/// <c>GeneralAvailabilityService</c> needs. <c>ShiftManagementService</c> and
/// <c>ShiftSignupService</c> are migrated in separate follow-ups (#541a, #541b)
/// and will get their own repositories (<c>IShiftManagementRepository</c>,
/// <c>IShiftSignupRepository</c>) per the section plan in
/// <c>docs/sections/Shifts.md</c>.
/// </para>
/// </remarks>
public interface IGeneralAvailabilityRepository
{
    /// <summary>
    /// Returns the single <see cref="GeneralAvailability"/> row for the given
    /// user + event pair, or <c>null</c> if none exists. Read-only
    /// (<c>AsNoTracking</c>).
    /// </summary>
    Task<GeneralAvailability?> GetByUserAndEventAsync(
        Guid userId, Guid eventSettingsId, CancellationToken ct = default);

    /// <summary>
    /// Returns every <see cref="GeneralAvailability"/> row for the given event.
    /// Read-only (<c>AsNoTracking</c>). Filtering by day offset is performed in
    /// memory by the caller because <c>AvailableDayOffsets</c> is stored as
    /// jsonb and EF cannot translate <c>List&lt;int&gt;.Contains</c> against
    /// jsonb columns.
    /// </summary>
    Task<IReadOnlyList<GeneralAvailability>> GetByEventAsync(
        Guid eventSettingsId, CancellationToken ct = default);

    /// <summary>
    /// Upserts the availability row for the given user + event pair. If no row
    /// exists, a new one is inserted with <paramref name="now"/> for both
    /// <c>CreatedAt</c> and <c>UpdatedAt</c>. If one exists, the
    /// <c>AvailableDayOffsets</c> and <c>UpdatedAt</c> fields are overwritten.
    /// </summary>
    Task UpsertAsync(
        Guid userId,
        Guid eventSettingsId,
        IReadOnlyList<int> dayOffsets,
        NodaTime.Instant now,
        CancellationToken ct = default);

    /// <summary>
    /// Deletes the availability row for the given user + event pair, if one
    /// exists. No-op when there is no matching row.
    /// </summary>
    Task DeleteAsync(
        Guid userId, Guid eventSettingsId, CancellationToken ct = default);
}
