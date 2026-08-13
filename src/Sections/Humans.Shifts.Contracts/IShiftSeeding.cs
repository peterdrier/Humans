using Humans.Domain.Entities;

using NodaTime;

namespace Humans.Shifts.Contracts;

/// <summary>
/// The dev-fixture verbs <c>Humans.Development</c>'s dashboard seeder drives:
/// stand up a demo burn with rotas, shifts and signups, and tear it back down.
/// </summary>
/// <remarks>
/// Teams' rule rather than Budget's. Budget carves a single method and takes
/// the seeding into the section when a *Shell* seeder drives the section's
/// write surface; <c>DevelopmentDashboardSeeder</c> builds a multi-section
/// fixture (teams, users, camps and shifts in one deterministic pass), so
/// taking the seeding in would steal another section's fixture. The verbs
/// come to the leaf instead and the seeder is unchanged.
///
/// <para>
/// Nothing else outside the section calls any of these — every other
/// <c>GetActiveAsync</c> / <c>GetByIdAsync</c> / <c>CreateAsync</c> /
/// <c>UpdateAsync</c> hit in the repo is a different service's member of the
/// same name; the burn other sections read is
/// <see cref="IBurnSettingsService"/>, which returns
/// <see cref="BurnSettingsInfo"/> rather than the entity.
/// </para>
///
/// <para>
/// <c>EventSettings</c> and <c>Rota</c> are still public
/// <c>Humans.Domain.Entities</c> types. These five members are the largest
/// remaining entity leak on the leaf and need request records before the
/// entities can turn internal at the section move. Recorded in
/// <c>local/shifts-g5/findings.md</c>.
/// </para>
/// </remarks>
public interface IShiftSeeding
{
    /// <summary>
    /// Gets the single active EventSettings, or null if none.
    /// </summary>
    Task<EventSettings?> GetActiveAsync();

    /// <summary>
    /// Gets an EventSettings by primary key.
    /// </summary>
    Task<EventSettings?> GetByIdAsync(Guid id);

    /// <summary>
    /// Creates a new EventSettings. Validates only one IsActive=true.
    /// </summary>
    Task CreateAsync(EventSettings entity);

    /// <summary>
    /// Updates an existing EventSettings.
    /// </summary>
    Task UpdateAsync(EventSettings entity);

    /// <summary>
    /// Creates a new rota. Validates team is a department and event is active.
    /// </summary>
    Task CreateRotaAsync(Rota rota, IReadOnlyList<Guid>? tagIds = null);

    /// <summary>
    /// Creates a new shift for a department rota. Validates rota ownership,
    /// period DayOffset range, and volunteer counts.
    /// </summary>
    Task<ShiftMutationResult> CreateShiftAsync(CreateShiftInput input);

    /// <summary>
    /// Deletes an event and all Shifts-owned rows beneath it: rotas, shifts,
    /// and shift signups. Requires the current authenticated user to hold the
    /// full Admin role.
    /// </summary>
    Task<int> DeleteEventAsync(Guid eventSettingsId, CancellationToken cancellationToken = default);
}

public sealed record CreateShiftInput(
    Guid RotaId,
    Guid TeamId,
    string? Description,
    int DayOffset,
    LocalTime StartTime,
    double DurationHours,
    int MinVolunteers,
    int MaxVolunteers,
    bool AdminOnly,
    bool IsAllDay);

public sealed record ShiftMutationResult(bool Succeeded, string Message, Guid? ShiftId = null)
{
    public static ShiftMutationResult Success(string message, Guid shiftId) => new(true, message, shiftId);
    public static ShiftMutationResult Failure(string message) => new(false, message);
}
