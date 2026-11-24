using Humans.Shifts.Domain;
using NodaTime;
using Humans.Base.Interfaces.Repositories;

namespace Humans.Shifts.Data;

internal partial interface IShiftManagementRepository : IRepository
{
    /// <summary>Null until a rota or knob edit creates the row.</summary>
    Task<EventSettings?> GetEventSettingsByIdAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<EventSettings>> GetAllEventSettingsAsync(CancellationToken ct = default);

    Task SaveEventSettingsAsync(EventSettings entity, EntityMutationMode mode, CancellationToken ct = default);

    /// <summary>One save for rota fields and optional tag replacement. Null keeps tags; empty clears them; unknown ids are ignored.</summary>
    Task SaveRotaAsync(
        Rota rota, EntityMutationMode mode, CancellationToken ct = default, IReadOnlyList<Guid>? tagIds = null);

    /// <summary>
    /// Writes only <c>TeamId</c> and <c>UpdatedAt</c>, so concurrent edits to other fields aren't clobbered. False when
    /// the rota doesn't exist.
    /// </summary>
    Task<bool> UpdateRotaTeamAssignmentAsync(
        Guid rotaId, Guid newTeamId, Instant updatedAt, CancellationToken ct = default);

    Task<Rota?> GetRotaAsync(Guid rotaId, RotaReadShape shape, CancellationToken ct = default);

    /// <summary>Rotas with tags, shifts and every signup, across all events. Null <paramref name="rotaIds"/> loads them all.</summary>
    Task<IReadOnlyList<Rota>> GetRotaRowsAsync(IReadOnlyCollection<Guid>? rotaIds, CancellationToken ct = default);

    /// <summary>One save, with every shift and signup. The service checks for confirmed signups first.</summary>
    Task DeleteRotaCascadeAsync(Guid rotaId, CancellationToken ct = default);

    /// <summary>
    /// Volunteer-visible rotas in the active event whose name contains <paramref name="query"/>, case-insensitive.
    /// The caller scores the candidates before applying its limit.
    /// </summary>
    Task<IReadOnlyList<Rota>> SearchVolunteerVisibleRotasAsync(
        string query, Guid eventSettingsId, CancellationToken ct = default);

    Task SaveShiftAsync(Shift shift, EntityMutationMode mode, CancellationToken ct = default);

    Task AddShiftsAsync(IEnumerable<Shift> shifts, CancellationToken ct = default);

    Task<Shift?> GetShiftAsync(Guid shiftId, ShiftReadShape shape, CancellationToken ct = default);

    /// <summary>One save, with every signup. The service checks for confirmed signups first.</summary>
    Task DeleteShiftCascadeAsync(Guid shiftId, CancellationToken ct = default);

    /// <summary>Distinct.</summary>
    Task<IReadOnlyList<int>> GetShiftDayOffsetsForRotaAsync(Guid rotaId, CancellationToken ct = default);

    /// <summary>With the rota; the flags control visibility filtering.</summary>
    Task<IReadOnlyList<Shift>> GetEventShiftsAsync(
        ShiftEventQuery query,
        CancellationToken ct = default);

    Task<IReadOnlyList<Rota>> GetRotasAsync(
        Guid eventSettingsId,
        IReadOnlyCollection<Guid> teamIds,
        RotaReadShape shape,
        CancellationToken ct = default);

    Task<IReadOnlyDictionary<Guid, int>> GetConfirmedSignupCountsByShiftAsync(
        IReadOnlyCollection<Guid> shiftIds,
        CancellationToken ct = default);

    Task<IReadOnlyList<ShiftTag>> GetTagsAsync(string? query = null, CancellationToken ct = default);

    /// <summary>Matches existing names case-insensitively.</summary>
    Task<ShiftTag> GetOrCreateTagAsync(string name, CancellationToken ct = default);

    /// <summary>Delete-then-insert in one save.</summary>
    Task SetVolunteerTagPreferencesAsync(
        Guid userId, IReadOnlyList<Guid> tagIds, CancellationToken ct = default);

    Task<VolunteerEventProfile?> GetVolunteerEventProfileAsync(
        Guid userId, CancellationToken ct = default);

    Task<IReadOnlyList<VolunteerEventProfile>> GetVolunteerEventProfilesByUserIdsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);

    Task AddVolunteerEventProfileAsync(
        VolunteerEventProfile profile, CancellationToken ct = default);

    Task UpdateVolunteerEventProfileAsync(
        VolunteerEventProfile profile, CancellationToken ct = default);

    /// <summary>Returns the number of rows removed.</summary>
    Task<int> DeleteVolunteerEventProfilesForUserAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>
    /// One save. Target wins on collision: the source's profile is removed when the target has one, and a source tag
    /// preference matching a target <c>(UserId, ShiftTagId)</c> is removed.
    /// </summary>
    Task ReassignProfilesAndTagPrefsToUserAsync(
        Guid sourceUserId,
        Guid targetUserId,
        Instant updatedAt,
        CancellationToken ct = default);
}

internal enum EntityMutationMode
{
    Add,
    Update
}

[Flags]
internal enum RotaReadShape
{
    None = 0,
    EventSettings = 1,
    Shifts = 2,
    ShiftSignups = 4,
    Tags = 8,
    ShiftsWithSignups = Shifts | ShiftSignups,
    View = EventSettings | Shifts | ShiftSignups | Tags
}

[Flags]
internal enum ShiftReadShape
{
    None = 0,
    Rota = 1,
    EventSettings = 2,
    ShiftSignups = 4,
    Context = Rota | EventSettings | ShiftSignups
}

[Flags]
internal enum ShiftEventQueryFlags
{
    None = 0,
    ExcludeAdminOnly = 1,
    ExcludeHiddenRotas = 2
}

internal sealed record ShiftEventQuery(
    Guid EventSettingsId,
    IReadOnlyCollection<Guid>? TeamIds = null,
    int? MinDayOffset = null,
    int? MaxDayOffset = null,
    ShiftEventQueryFlags Flags = ShiftEventQueryFlags.None);
