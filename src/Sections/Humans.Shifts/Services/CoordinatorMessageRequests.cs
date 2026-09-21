namespace Humans.Shifts.Services;

/// <summary>
/// Payload for a coordinator "email a rota" message to a single signup.
/// <see cref="ShiftLines"/> are pre-rendered, chronologically-ordered shift labels
/// for the recipient on this rota (e.g. "Mon July 6 @ 19:30") — <see cref="ShiftsEmails"/>
/// HTML-encodes them, it does not parse or sort them.
/// </summary>
/// <remarks>
/// Lived on <c>Humans.Email.Contracts</c> until Shifts took its own templates; only
/// <c>RotaCoordinatorMessageService</c> ever built one (peterdrier/Humans#1651).
/// </remarks>
internal sealed record CoordinatorRotaMessageRequest(
    string RecipientEmail,
    string RecipientName,
    string SenderName,
    string? SenderEmail,
    string RotaName,
    string MessageText,
    IReadOnlyList<string> ShiftLines,
    string? Culture = null);

/// <summary>
/// Payload for a coordinator team-level "email all rotas" message to a single signup.
/// <see cref="ShiftGroups"/> are pre-rendered, per-rota lists of chronologically-ordered
/// shift labels for the recipient (each rota's shifts in the rota's own timezone).
/// </summary>
internal sealed record CoordinatorTeamRotasMessageRequest(
    string RecipientEmail,
    string RecipientName,
    string SenderName,
    string? SenderEmail,
    string TeamName,
    string MessageText,
    IReadOnlyList<CoordinatorRotaShiftGroup> ShiftGroups,
    string? Culture = null);

/// <summary>
/// A recipient's shifts on a single rota, ready for the team-level coordinator message.
/// <see cref="ShiftLines"/> are already chronological and formatted in the rota's timezone.
/// </summary>
/// <remarks>
/// Named <c>RotaShiftGroup</c> on <c>Humans.Email.Contracts</c>; renamed on the move because
/// Shifts already owns an unrelated <c>Humans.Shifts.Models.RotaShiftGroup</c> view model.
/// </remarks>
internal sealed record CoordinatorRotaShiftGroup(string RotaName, IReadOnlyList<string> ShiftLines);
