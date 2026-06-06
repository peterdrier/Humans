namespace Humans.Application.DTOs.Shifts;

/// <summary>
/// Per-user confirmed-shift totals for the Shift Summary view, aggregated in the
/// Shifts repository from confirmed <c>ShiftSignup</c> rows joined to their shift.
/// <see cref="Hours"/> is the sum of <c>Shift.Duration</c> (in hours);
/// <see cref="Count"/> is the number of confirmed signups in the requested scope.
/// </summary>
public sealed record ConfirmedUserShiftTotal(Guid UserId, double Hours, int Count);

/// <summary>Which scope a <see cref="ShiftSummary"/> was built for.</summary>
public enum ShiftSummaryScope { Global, Team, Rota }

/// <summary>
/// One flat row per human with ≥1 confirmed signup in scope (Table 1).
/// <see cref="CampId"/> / <see cref="CampName"/> are null for a human with no
/// active camp membership this public year (rendered as the "no camp" bucket).
/// </summary>
public sealed record ShiftSummaryHumanRow(
    Guid UserId, string Name, Guid? CampId, string? CampName, double Hours, int Count);

/// <summary>
/// One pivot row per camp (Table 2): the active-camp roster left-joined with the
/// confirmed-signup totals, so a camp with nobody in scope surfaces as a zero
/// row. <see cref="CampId"/> is null for the campless bucket — humans signed up
/// with no active camp membership.
/// </summary>
public sealed record ShiftSummaryCampRow(
    Guid? CampId, string? CampName, int People, double Hours, int Count);

/// <summary>Global-page drill-down link to a team's summary page.</summary>
public sealed record ShiftSummaryTeamLink(Guid TeamId, string Name, string Slug);

/// <summary>Team-page drill-down link to a single rota's summary page.</summary>
public sealed record ShiftSummaryRotaLink(Guid RotaId, string Name);

/// <summary>
/// All data for one Shift Summary page at one scope (global / team / rota): the
/// flat per-human table, the by-camp pivot table, and drill-down links. Rows are
/// returned unsorted — the controller applies display ordering.
/// </summary>
public sealed record ShiftSummary(
    ShiftSummaryScope Scope,
    string? TeamName,
    string? TeamSlug,
    string? RotaName,
    Guid? RotaId,
    IReadOnlyList<ShiftSummaryHumanRow> Humans,
    IReadOnlyList<ShiftSummaryCampRow> Camps,
    IReadOnlyList<ShiftSummaryTeamLink> TeamLinks,
    IReadOnlyList<ShiftSummaryRotaLink> RotaLinks);
