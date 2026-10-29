namespace Humans.Shifts.Services.Dtos;

/// <summary>
/// The start day (offset from gate opening) of each build sub-period; each runs to the next one's start, and
/// the finishing weekend to day 0.
/// </summary>
internal sealed record BuildSubPeriodStarts(int FirstCrew, int SetupWeek, int PreEventWeek, int FinishingWeekend);
