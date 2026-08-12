namespace Humans.UI.Models;

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
