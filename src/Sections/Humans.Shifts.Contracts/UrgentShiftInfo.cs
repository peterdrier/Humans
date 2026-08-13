using Humans.Domain.Enums;

using NodaTime;

namespace Humans.Shifts.Contracts;

/// <summary>
/// One shift row as consumers outside the section render it: the shift's own
/// fields, its rota's header fields, the department name, the event-relative
/// scalars already resolved, and the urgency arithmetic.
/// </summary>
/// <remarks>
/// Replaces the entity-bearing <c>UrgentShift(Shift, …)</c> record. The shape
/// is deliberately the *row*, not the entity graph: <see cref="AbsoluteStart"/>,
/// <see cref="AbsoluteEnd"/>, <see cref="Date"/> and <see cref="Period"/> are
/// resolved inside the section — where the event is in scope — so no consumer
/// has to fetch the burn separately just to render a shift
/// (nobodies-collective/Humans#866, G5; the same call as
/// <see cref="ShiftSignupSummary"/>).
///
/// <para>
/// The nesting is not decoration: <see cref="Shift"/> and <see cref="Rota"/>
/// carry the member names the section's four rota partials already bind, so the
/// markup reads the projection exactly as it read the entities.
/// </para>
/// </remarks>
/// <param name="Shift">The shift's own fields.</param>
/// <param name="Rota">The owning rota's header fields.</param>
/// <param name="DepartmentName">Owning department's display name, stitched in via <c>ITeamService</c>.</param>
/// <param name="Date">Calendar date, resolved against the event's gate-opening date.</param>
/// <param name="Period">Build / Event / Strike, resolved against the event's dates.</param>
/// <param name="AbsoluteStart">Absolute start instant, resolved in the event's time zone.</param>
/// <param name="AbsoluteEnd">Absolute end instant, resolved in the event's time zone.</param>
/// <param name="UrgencyScore">Computed urgency score; 0 when the shift is full.</param>
/// <param name="ConfirmedCount">Confirmed signups on this shift.</param>
/// <param name="RemainingSlots">Open slots — max volunteers minus confirmed.</param>
/// <param name="Signups">
/// Confirmed + pending signups with display names stitched in. Empty unless the
/// query asked for them (<see cref="ShiftBrowseQueryFlags.IncludeSignups"/>).
/// </param>
public sealed record UrgentShiftInfo(
    ShiftInfo Shift,
    RotaInfo Rota,
    string DepartmentName,
    LocalDate Date,
    ShiftPeriod Period,
    Instant AbsoluteStart,
    Instant AbsoluteEnd,
    double UrgencyScore,
    int ConfirmedCount,
    int RemainingSlots,
    IReadOnlyList<ShiftSignupInfo> Signups);

/// <summary>
/// A shift's own fields, flattened off the entity. Member names match
/// <c>Shift</c>'s so the rota partials bind unchanged.
/// </summary>
/// <param name="Id">Shift row id.</param>
/// <param name="RotaId">FK to the owning rota.</param>
/// <param name="Description">Optional per-shift duties text.</param>
/// <param name="DayOffset">Day offset from gate opening; negative = build.</param>
/// <param name="StartTime">Stored wall-clock start. Don't-care when <paramref name="IsAllDay"/>.</param>
/// <param name="DurationHours">Stored duration in hours. Don't-care when <paramref name="IsAllDay"/>.</param>
/// <param name="MinVolunteers">Understaffed threshold.</param>
/// <param name="MaxVolunteers">Hard capacity ceiling.</param>
/// <param name="AdminOnly">Restricted to coordinators/admins.</param>
/// <param name="IsAllDay">All-day (build/strike) shift.</param>
/// <param name="IsEarlyEntry">Falls in the build period — <paramref name="DayOffset"/> &lt; 0.</param>
public sealed record ShiftInfo(
    Guid Id,
    Guid RotaId,
    string? Description,
    int DayOffset,
    LocalTime StartTime,
    double DurationHours,
    int MinVolunteers,
    int MaxVolunteers,
    bool AdminOnly,
    bool IsAllDay,
    bool IsEarlyEntry);

/// <summary>
/// A rota's header fields, flattened off the entity. Member names match
/// <c>Rota</c>'s so the rota partials bind unchanged.
/// </summary>
/// <param name="Id">Rota row id.</param>
/// <param name="EventSettingsId">Event the rota belongs to.</param>
/// <param name="TeamId">Owning department team.</param>
/// <param name="Name">Display name.</param>
/// <param name="Description">Optional markdown description.</param>
/// <param name="PracticalInfo">Meeting point / what to bring, markdown.</param>
/// <param name="Priority">Priority level driving urgency scoring.</param>
/// <param name="Policy">Whether signups auto-confirm or need approval.</param>
/// <param name="Period">Build / Event / Strike, as set by the coordinator.</param>
/// <param name="IsVisibleToVolunteers">Whether volunteers see the rota on browse.</param>
/// <param name="Tags">Tags applied to the rota.</param>
public sealed record RotaInfo(
    Guid Id,
    Guid EventSettingsId,
    Guid TeamId,
    string Name,
    string? Description,
    string? PracticalInfo,
    ShiftPriority Priority,
    SignupPolicy Policy,
    RotaPeriod Period,
    bool IsVisibleToVolunteers,
    IReadOnlyList<ShiftTagSummary> Tags);

/// <summary>
/// One person signed up to a shift, with their display name stitched in.
/// </summary>
public sealed record ShiftSignupInfo(Guid UserId, string DisplayName, SignupStatus Status);
