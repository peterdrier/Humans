namespace Humans.Shifts.Models;

/// <summary>
/// Post-event statistics dashboard: completion and no-show rates across the whole event,
/// broken down by department and period.
/// </summary>
internal sealed record PostEventStats(
    int TotalShifts,
    int ShiftsWithData,
    int TotalConfirmed,
    int TotalNoShow,
    IReadOnlyList<PostEventDepartmentRow> Departments)
{
    /// <summary>
    /// No-show rate as a percentage 0..100, or 0 when no signups to measure.
    /// </summary>
    public int NoShowPct => PostEventPeriodRow.NoShowPercentage(TotalConfirmed, TotalNoShow);

    /// <summary>
    /// Completion rate as a percentage 0..100, or 0 when no signups to measure.
    /// </summary>
    public int CompletionPct => PostEventPeriodRow.CompletionPercentage(TotalConfirmed, TotalNoShow);
}

/// <summary>
/// Per-department row for the post-event stats dashboard.
/// Includes Build, Event, and Strike period breakdowns.
/// </summary>
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
    // NoShowPct/CompletionPct: see PostEventStats above — same 0-when-empty rule.
    public int NoShowPct => PostEventPeriodRow.NoShowPercentage(TotalConfirmed, TotalNoShow);

    public int CompletionPct => PostEventPeriodRow.CompletionPercentage(TotalConfirmed, TotalNoShow);
}

/// <summary>
/// Confirmed/no-show counts for a single shift period within a department.
/// </summary>
internal sealed record PostEventPeriodRow(int TotalConfirmed, int TotalNoShow)
{
    // NoShowPct/CompletionPct: see PostEventStats above — same 0-when-empty rule.
    public int NoShowPct => PostEventPeriodRow.NoShowPercentage(TotalConfirmed, TotalNoShow);

    public int CompletionPct => PostEventPeriodRow.CompletionPercentage(TotalConfirmed, TotalNoShow);

    internal static int NoShowPercentage(int confirmed, int noShow) => confirmed + noShow > 0
        ? (int)Math.Round(100.0 * noShow / (confirmed + noShow), MidpointRounding.AwayFromZero)
        : 0;

    internal static int CompletionPercentage(int confirmed, int noShow) => confirmed + noShow > 0
        ? Math.Clamp(100 - NoShowPercentage(confirmed, noShow), 0, 100)
        : 0;
}
