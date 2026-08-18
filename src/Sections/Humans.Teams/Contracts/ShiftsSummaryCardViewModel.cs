namespace Humans.Teams.Contracts;

/// <summary>
/// Render model for <c>_ShiftsSummaryCard</c>. Teams' — not Shifts': the only producer is
/// <c>TeamController.MapShiftsSummary</c> and the only renderer is <c>Team/Details</c>
/// (plus Shell's widget gallery). No Shifts type appears on it
/// (nobodies-collective/Humans#866, G5 lane 4b-i). Under <c>Contracts/</c> rather than
/// <c>Models/</c> because Shell's widget gallery binds it (HUM0034).
/// </summary>
public class ShiftsSummaryCardViewModel
{
    public int TotalSlots { get; set; }
    public int ConfirmedCount { get; set; }
    public int PendingCount { get; set; }
    public int UniqueVolunteerCount { get; set; }
    public string ShiftsUrl { get; set; } = "";
    public bool CanManageShifts { get; set; }

    /// <summary>
    /// When > 0, indicates this summary includes data from child teams.
    /// </summary>
    public int IncludesSubTeamCount { get; set; }
}
