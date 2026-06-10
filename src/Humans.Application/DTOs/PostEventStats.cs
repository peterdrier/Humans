namespace Humans.Application.DTOs;

/// <summary>
/// Post-event statistics dashboard: completion and no-show rates across the whole event,
/// broken down by department and period.
/// </summary>
public record PostEventStats(
    int TotalShifts,
    int ShiftsWithData,
    int TotalConfirmed,
    int TotalNoShow,
    IReadOnlyList<PostEventDepartmentRow> Departments)
{
    /// <summary>
    /// No-show rate as a percentage 0..100, or 0 when no signups to measure.
    /// </summary>
    public int NoShowPct => (TotalConfirmed + TotalNoShow) > 0
        ? (int)Math.Round(100.0 * TotalNoShow / (TotalConfirmed + TotalNoShow), MidpointRounding.AwayFromZero)
        : 0;

    /// <summary>
    /// Completion rate as a percentage 0..100, or 0 when no signups to measure.
    /// </summary>
    public int CompletionPct => (TotalConfirmed + TotalNoShow) > 0
        ? Math.Clamp(100 - NoShowPct, 0, 100)
        : 0;
}

/// <summary>
/// Per-department row for the post-event stats dashboard.
/// Includes Build, Event, and Strike period breakdowns.
/// </summary>
public record PostEventDepartmentRow(
    Guid DepartmentId,
    string DepartmentName,
    string? DepartmentSlug,
    int TotalConfirmed,
    int TotalNoShow,
    PostEventPeriodRow Build,
    PostEventPeriodRow Event,
    PostEventPeriodRow Strike)
{
    /// <summary>
    /// No-show rate as a percentage 0..100, or 0 when no signups to measure.
    /// </summary>
    public int NoShowPct => (TotalConfirmed + TotalNoShow) > 0
        ? (int)Math.Round(100.0 * TotalNoShow / (TotalConfirmed + TotalNoShow), MidpointRounding.AwayFromZero)
        : 0;

    /// <summary>
    /// Completion rate as a percentage 0..100, or 0 when no signups to measure.
    /// </summary>
    public int CompletionPct => (TotalConfirmed + TotalNoShow) > 0
        ? Math.Clamp(100 - NoShowPct, 0, 100)
        : 0;
}

/// <summary>
/// Confirmed/no-show counts for a single shift period within a department.
/// </summary>
public record PostEventPeriodRow(int TotalConfirmed, int TotalNoShow)
{
    /// <summary>
    /// No-show rate as a percentage 0..100, or 0 when no signups to measure.
    /// </summary>
    public int NoShowPct => (TotalConfirmed + TotalNoShow) > 0
        ? (int)Math.Round(100.0 * TotalNoShow / (TotalConfirmed + TotalNoShow), MidpointRounding.AwayFromZero)
        : 0;

    /// <summary>
    /// Completion rate as a percentage 0..100, or 0 when no signups to measure.
    /// </summary>
    public int CompletionPct => (TotalConfirmed + TotalNoShow) > 0
        ? Math.Clamp(100 - NoShowPct, 0, 100)
        : 0;
}
