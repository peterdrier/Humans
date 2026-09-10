
using NodaTime;

namespace Humans.Shifts.Contracts;

/// <summary>
/// The dev-fixture verbs <c>Humans.Development</c>'s dashboard seeder drives:
/// stand up a demo burn with rotas, shifts and signups, and tear it back down.
/// </summary>
/// <remarks>
/// <para>
/// Nothing else outside the section calls any of these — every other
/// <c>CreateAsync</c> / <c>UpdateAsync</c> hit in the repo is a different
/// service's member of the same name; the burn other sections read is
/// <see cref="IBurnSettingsService"/>, which returns
/// <see cref="BurnSettingsInfo"/> rather than the entity.
/// </para>
///
/// <para>
/// The verbs take input records, never <c>EventSettings</c> / <c>Rota</c> rows.
/// </para>
/// </remarks>
public interface IShiftSeeding
{
    /// <summary>
    /// Deactivates the currently active burn so a newly created one can take
    /// over (only one may be active). Returns <c>true</c> when one was
    /// deactivated, <c>false</c> when none was active.
    /// </summary>
    Task<bool> DeactivateActiveBurnAsync();

    /// <summary>
    /// Creates a burn and makes it the active one. Fails if another burn is
    /// already active — call <see cref="DeactivateActiveBurnAsync"/> first.
    /// </summary>
    Task CreateBurnAsync(CreateBurnInput input);

    /// <summary>
    /// Creates a rota on a department team of an active burn, optionally
    /// tagging it. Returns the new rota's id.
    /// </summary>
    Task<Guid> CreateRotaAsync(CreateRotaInput input, IReadOnlyList<Guid>? tagIds = null);

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

/// <summary>The burn fields a seeded fixture sets; everything else takes its schema default.</summary>
public sealed record CreateBurnInput(
    Guid Id,
    string EventName,
    int Year,
    string TimeZoneId,
    LocalDate GateOpeningDate,
    int BuildStartOffset,
    int EventEndOffset,
    int StrikeEndOffset,
    bool IsShiftBrowsingOpen);

/// <summary>The rota fields a seeded fixture sets; everything else takes its schema default.</summary>
public sealed record CreateRotaInput(
    Guid TeamId,
    Guid EventSettingsId,
    string Name,
    ShiftPriority Priority,
    SignupPolicy Policy,
    RotaPeriod Period,
    bool IsVisibleToVolunteers);

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
