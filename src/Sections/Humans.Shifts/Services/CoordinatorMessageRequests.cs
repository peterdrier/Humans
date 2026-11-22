using NodaTime;

namespace Humans.Shifts.Services;

/// <summary>
/// <see cref="Shifts"/> carry their rota timezone; <see cref="ShiftsEmails"/> orders and formats them.
/// <see cref="IncludeShifts"/> false drops the section, unlike an empty list, which prints the "no shifts yet" note.
/// </summary>
internal sealed record CoordinatorRotaMessageRequest(
    Guid RecipientUserId,
    string RecipientEmail,
    string RecipientName,
    string SenderName,
    string? SenderEmail,
    string RotaName,
    string MessageText,
    IReadOnlyList<(ZonedDateTime Start, bool IsAllDay)> Shifts,
    bool IncludeShifts = true,
    string? Culture = null);

/// <summary>
/// <see cref="ShiftGroups"/> carry each rota's zoned shift starts. <see cref="IncludeShifts"/> false
/// drops the shift section.
/// </summary>
internal sealed record CoordinatorTeamRotasMessageRequest(
    Guid RecipientUserId,
    string RecipientEmail,
    string RecipientName,
    string SenderName,
    string? SenderEmail,
    string TeamName,
    string MessageText,
    IReadOnlyList<CoordinatorRotaShiftGroup> ShiftGroups,
    bool IncludeShifts = true,
    string? Culture = null);

internal sealed record CoordinatorRotaShiftGroup(string RotaName, IReadOnlyList<(ZonedDateTime Start, bool IsAllDay)> Shifts);
