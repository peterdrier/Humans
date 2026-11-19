namespace Humans.Shifts.Services;

internal sealed record DailyStaffingData(
    int DayOffset,
    string DateLabel,
    int ConfirmedCount,
    int TotalSlots,
    int MinSlots,
    string Period);

/// <summary>
/// Hours are each shift's overlap with the day times its MaxVolunteers; an all-day shift counts its 10h window.
/// </summary>
internal sealed record DailyStaffingHours(
    int DayOffset,
    string DateLabel,
    double EssentialHours,
    double ImportantHours,
    double NormalHours);

internal sealed record ShiftStaffingSnapshot(
    IReadOnlyList<DailyStaffingData> StaffingData,
    IReadOnlyList<DailyStaffingHours> StaffingHours)
{
    public static ShiftStaffingSnapshot Empty { get; } = new([], []);
}
