using Humans.Application.DTOs.Shifts;

namespace Humans.Web.Models;

/// <summary>
/// View model for the Shift Summary by Camp page (one shape across the global,
/// team-set, and single-rota scopes). Rows arrive already sorted for display from
/// <see cref="Controllers.ShiftsController"/>; the view only renders.
/// </summary>
public sealed class ShiftSummaryViewModel
{
    public required string EventName { get; init; }
    public required ShiftSummaryScope Scope { get; init; }

    /// <summary>Set for the team and rota scopes — drives the breadcrumb + heading.</summary>
    public string? TeamName { get; init; }
    public string? TeamSlug { get; init; }

    /// <summary>Set for the rota scope only.</summary>
    public string? RotaName { get; init; }
    public Guid? RotaId { get; init; }

    /// <summary>Table 1 — one row per human with ≥1 confirmed signup in scope.</summary>
    public required IReadOnlyList<ShiftSummaryHumanRow> Humans { get; init; }

    /// <summary>Table 2 — by-camp pivot (active-camp roster left-joined, campless last).</summary>
    public required IReadOnlyList<ShiftSummaryCampRow> Camps { get; init; }

    /// <summary>Global scope: drill-down links to each department's summary page.</summary>
    public required IReadOnlyList<ShiftSummaryTeamLink> TeamLinks { get; init; }

    /// <summary>Team scope: drill-down links to each rota's summary page.</summary>
    public required IReadOnlyList<ShiftSummaryRotaLink> RotaLinks { get; init; }
}
