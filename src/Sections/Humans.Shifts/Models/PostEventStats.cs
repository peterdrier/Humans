namespace Humans.Shifts.Models;

internal sealed record PostEventStats(
    int TotalShifts,
    int ShiftsWithData,
    int TotalConfirmed,
    int TotalNoShow,
    IReadOnlyList<PostEventDepartmentRow> Departments)
{
    /// <summary>0 when there are no signups to measure.</summary>
    public int NoShowPct => PostEventPeriodRow.NoShowPercentage(TotalConfirmed, TotalNoShow);

    /// <summary>0 when there are no signups to measure.</summary>
    public int CompletionPct => PostEventPeriodRow.CompletionPercentage(TotalConfirmed, TotalNoShow);
}

internal sealed record PostEventDepartmentRow(
    Guid DepartmentId,
    string DepartmentName,
    string? DepartmentSlug,
    int TotalConfirmed,
    int TotalNoShow,
    PostEventPeriodRow Build,
    PostEventPeriodRow Event,
    PostEventPeriodRow Strike)
{
    public int NoShowPct => PostEventPeriodRow.NoShowPercentage(TotalConfirmed, TotalNoShow);

    public int CompletionPct => PostEventPeriodRow.CompletionPercentage(TotalConfirmed, TotalNoShow);
}

internal sealed record PostEventPeriodRow(int TotalConfirmed, int TotalNoShow)
{
    public int NoShowPct => PostEventPeriodRow.NoShowPercentage(TotalConfirmed, TotalNoShow);

    public int CompletionPct => PostEventPeriodRow.CompletionPercentage(TotalConfirmed, TotalNoShow);

    internal static int NoShowPercentage(int confirmed, int noShow) => confirmed + noShow > 0
        ? (int)Math.Round(100.0 * noShow / (confirmed + noShow), MidpointRounding.AwayFromZero)
        : 0;

    internal static int CompletionPercentage(int confirmed, int noShow) => confirmed + noShow > 0
        ? Math.Clamp(100 - NoShowPercentage(confirmed, noShow), 0, 100)
        : 0;
}
