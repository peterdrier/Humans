using Humans.Shifts.Services.Dtos;

namespace Humans.Shifts.Models;

/// <summary>Rows arrive sorted by the controller; the view only renders.</summary>
internal sealed class ShiftSummaryViewModel
{
    public required string EventName { get; init; }
    public required ShiftSummaryScope Scope { get; init; }

    /// <summary>Set for the team and rota scopes.</summary>
    public string? TeamName { get; init; }
    public string? TeamSlug { get; init; }

    public string? RotaName { get; init; }
    public Guid? RotaId { get; init; }

    public required IReadOnlyList<ShiftSummaryHumanRow> Humans { get; init; }

    public required IReadOnlyList<ShiftSummaryCampRow> Camps { get; init; }

    public required IReadOnlyList<ShiftSummaryTeamLink> TeamLinks { get; init; }

    public required IReadOnlyList<ShiftSummaryRotaLink> RotaLinks { get; init; }
}
