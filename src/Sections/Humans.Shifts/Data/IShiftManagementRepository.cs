using Humans.Shifts.Services.Dtos;
using Humans.Shifts.Services;
using Humans.Shifts.Contracts;
using Humans.Shifts.Domain;
using NodaTime;
using Humans.Base.Interfaces.Repositories;

namespace Humans.Shifts.Data;

internal partial interface IShiftManagementRepository : IRepository
{
    /// <summary>Null until a rota or knob edit creates the row.</summary>
    Task<EventSettings?> GetEventSettingsByIdAsync(Guid id, CancellationToken ct = default);

    Task SaveEventSettingsAsync(EventSettings entity, EntityMutationMode mode, CancellationToken ct = default);

    /// <summary>Also deletes every Shifts-owned row beneath it. Returns the number of event rows deleted.</summary>
    Task<int> DeleteEventCascadeAsync(Guid eventSettingsId, CancellationToken ct = default);

    Task SaveRotaAsync(Rota rota, EntityMutationMode mode, CancellationToken ct = default);

    /// <summary>
    /// Writes only <c>TeamId</c> and <c>UpdatedAt</c>, so concurrent edits to other fields aren't clobbered. False when
    /// the rota doesn't exist.
    /// </summary>
    Task<bool> UpdateRotaTeamAssignmentAsync(
        Guid rotaId, Guid newTeamId, Instant updatedAt, CancellationToken ct = default);

    Task<Rota?> GetRotaAsync(Guid rotaId, RotaReadShape shape, CancellationToken ct = default);

    /// <summary>One save, with every shift and signup. The service checks for confirmed signups first.</summary>
    Task DeleteRotaCascadeAsync(Guid rotaId, CancellationToken ct = default);

    /// <summary>
    /// Volunteer-visible rotas in the active event whose name contains <paramref name="query"/>, case-insensitive. At
    /// most <paramref name="max"/>, unordered: the caller ranks.
    /// </summary>
    Task<IReadOnlyList<Rota>> SearchVolunteerVisibleRotasAsync(
        string query, Guid eventSettingsId, int max, CancellationToken ct = default);

    /// <summary>Replaces existing tags. Unknown tag ids are ignored.</summary>
    Task SetRotaTagsAsync(Guid rotaId, IReadOnlyList<Guid> tagIds, CancellationToken ct = default);

    Task SaveShiftAsync(Shift shift, EntityMutationMode mode, CancellationToken ct = default);

    Task AddShiftsAsync(IEnumerable<Shift> shifts, CancellationToken ct = default);

    Task<Shift?> GetShiftAsync(Guid shiftId, ShiftReadShape shape, CancellationToken ct = default);

    /// <summary>One save, with every signup. The service checks for confirmed signups first.</summary>
    Task DeleteShiftCascadeAsync(Guid shiftId, CancellationToken ct = default);

    /// <summary>Distinct.</summary>
    Task<IReadOnlyList<int>> GetShiftDayOffsetsForRotaAsync(Guid rotaId, CancellationToken ct = default);

    /// <summary>With the rota; the flags control visibility filtering and eager loads.</summary>
    Task<IReadOnlyList<Shift>> GetEventShiftsAsync(
        ShiftEventQuery query,
        CancellationToken ct = default);

    Task<IReadOnlyList<Rota>> GetRotasAsync(
        Guid eventSettingsId,
        IReadOnlyCollection<Guid> teamIds,
        RotaReadShape shape,
        CancellationToken ct = default);

    Task<IReadOnlyList<Guid>> GetTeamIdsWithRotasInEventAsync(
        Guid eventSettingsId,
        CancellationToken ct = default);

    Task<IReadOnlyDictionary<Guid, int>> GetConfirmedSignupCountsByShiftAsync(
        IReadOnlyCollection<Guid> shiftIds,
        CancellationToken ct = default);

    /// <summary>
    /// Confirmed signups only, for the active event. No <paramref name="teamIds"/> or <paramref name="rotaId"/> spans
    /// every team; <paramref name="teamIds"/> narrows to a team-set and <paramref name="rotaId"/> to one rota, taking
    /// precedence.
    /// </summary>
    Task<IReadOnlyList<ConfirmedUserShiftTotal>> GetConfirmedUserShiftTotalsAsync(
        Guid eventSettingsId,
        IReadOnlyCollection<Guid>? teamIds = null,
        Guid? rotaId = null,
        CancellationToken ct = default);

    /// <summary>Distinct users with a non-cancelled signup on any of the shifts.</summary>
    Task<IReadOnlyList<Guid>> GetEngagedUserIdsForShiftsAsync(
        IReadOnlyCollection<Guid> shiftIds,
        CancellationToken ct = default);

    Task<int> GetStalePendingSignupCountAsync(
        IReadOnlyCollection<Guid> shiftIds,
        Instant staleThreshold,
        CancellationToken ct = default);

    /// <summary>Deduped by <c>SignupBlockId ?? Id</c>, so a range counts once.</summary>
    Task<IReadOnlyDictionary<Guid, int>> GetPendingSignupCountsByTeamAsync(
        Guid eventSettingsId,
        int? minDayOffset,
        int? maxDayOffset,
        CancellationToken ct = default);

    Task<IReadOnlyList<Instant>> GetSignupCreatedAtsInWindowAsync(
        Guid eventSettingsId,
        Instant fromInclusive,
        Instant toExclusive,
        int? minDayOffset,
        int? maxDayOffset,
        CancellationToken ct = default);

    /// <summary>Shifts with no Confirmed or NoShow signups are omitted.</summary>
    Task<IReadOnlyList<ShiftSignupStatusCount>> GetSignupStatusCountsForEventAsync(
        Guid eventSettingsId,
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
    ExcludeHiddenRotas = 2,
    IncludeSignups = 4,
    IncludeRotaTags = 8
}

internal sealed record ShiftEventQuery(
    Guid EventSettingsId,
    IReadOnlyCollection<Guid>? TeamIds = null,
    int? MinDayOffset = null,
    int? MaxDayOffset = null,
    ShiftEventQueryFlags Flags = ShiftEventQueryFlags.None);

internal sealed record ShiftSignupStatusCount(
    Guid ShiftId,
    Guid RotaTeamId,
    int DayOffset,
    int ConfirmedCount,
    int NoShowCount);
