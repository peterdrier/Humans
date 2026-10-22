namespace Humans.Teams.Contracts;

/// <summary>Under <c>Contracts/</c>, not <c>Models/</c>, because Shell's widget gallery binds it.</summary>
public class ShiftsSummaryCardViewModel
{
    public int TotalSlots { get; set; }
    public int ConfirmedCount { get; set; }
    public int PendingCount { get; set; }
    public int UniqueVolunteerCount { get; set; }
    public string ShiftsUrl { get; set; } = "";
    public bool CanManageShifts { get; set; }

    /// <summary>Greater than zero when the summary includes child teams.</summary>
    public int IncludesSubTeamCount { get; set; }
}
