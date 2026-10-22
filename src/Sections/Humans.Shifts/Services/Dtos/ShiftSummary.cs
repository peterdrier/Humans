namespace Humans.Shifts.Services.Dtos;

/// <summary>
/// <see cref="Hours"/> counts an all-day shift as the all-day window, not its 24h sentinel duration.
/// </summary>
internal sealed record ConfirmedUserShiftTotal(Guid UserId, double Hours, int Count);

internal enum ShiftSummaryScope { Global, Team, Rota }

/// <summary>Null camp: no active camp membership this public year, the "no camp" bucket.</summary>
internal sealed record ShiftSummaryHumanRow(
    Guid UserId, string Name, Guid? CampId, string? CampName, double Hours, int Count);

/// <summary>
/// The active-camp roster left-joined with the totals, so a camp with nobody in scope is a zero row. Null
/// <see cref="CampId"/> is the campless bucket.
/// </summary>
internal sealed record ShiftSummaryCampRow(
    Guid? CampId, string? CampName, int People, double Hours, int Count);

internal sealed record ShiftSummaryTeamLink(Guid TeamId, string Name, string Slug);

internal sealed record ShiftSummaryRotaLink(Guid RotaId, string Name);

/// <summary>Rows are unsorted; the controller orders them for display.</summary>
internal sealed record ShiftSummary(
    ShiftSummaryScope Scope,
    string? TeamName,
    string? TeamSlug,
    string? RotaName,
    Guid? RotaId,
    IReadOnlyList<ShiftSummaryHumanRow> Humans,
    IReadOnlyList<ShiftSummaryCampRow> Camps,
    IReadOnlyList<ShiftSummaryTeamLink> TeamLinks,
    IReadOnlyList<ShiftSummaryRotaLink> RotaLinks);
