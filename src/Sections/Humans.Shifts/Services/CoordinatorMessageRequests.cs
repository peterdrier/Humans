namespace Humans.Shifts.Services;

/// <summary>
/// <see cref="ShiftLines"/> are pre-rendered and sorted; <see cref="ShiftsEmails"/> only encodes them.
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
    IReadOnlyList<string> ShiftLines,
    bool IncludeShifts = true,
    string? Culture = null);

/// <summary>
/// <see cref="ShiftGroups"/> are pre-rendered, each rota in its own timezone. <see cref="IncludeShifts"/> false
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

internal sealed record CoordinatorRotaShiftGroup(string RotaName, IReadOnlyList<string> ShiftLines);
