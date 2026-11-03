
using NodaTime;

namespace Humans.Shifts.Contracts;

/// <summary>
/// The row, not the entity graph: the event-relative fields are resolved inside the section so no consumer fetches
/// the burn, and <see cref="Shift"/> and <see cref="Rota"/> keep the member names the rota partials bind.
/// <see cref="UrgencyScore"/> is 0 when full; <see cref="Signups"/> is empty unless
/// <see cref="ShiftBrowseQueryFlags.IncludeSignups"/> asked.
/// </summary>
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
/// Member names match <c>Shift</c>'s so the rota partials bind unchanged. <c>StartTime</c> and
/// <c>DurationHours</c> are don't-care when <c>IsAllDay</c>.
/// </summary>
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

/// <summary>Member names match <c>Rota</c>'s so the rota partials bind unchanged.</summary>
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

public sealed record ShiftSignupInfo(Guid UserId, string DisplayName, SignupStatus Status);
