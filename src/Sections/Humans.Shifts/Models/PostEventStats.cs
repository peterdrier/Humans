namespace Humans.Shifts.Models;

internal sealed record PostEventStats(
    int TotalShifts,
    int ShiftsWithData,
    int TotalConfirmed,
    int TotalNoShow,
    IReadOnlyList<PostEventDepartmentRow> Departments)
{
    /// <summary>0 when there are no signups to measure.</summary>
    public int NoShowPct => (TotalConfirmed + TotalNoShow) > 0
        ? (int)Math.Round(100.0 * TotalNoShow / (TotalConfirmed + TotalNoShow), MidpointRounding.AwayFromZero)
        : 0;

    /// <summary>0 when there are no signups to measure.</summary>
    public int CompletionPct => (TotalConfirmed + TotalNoShow) > 0
        ? Math.Clamp(100 - NoShowPct, 0, 100)
        : 0;
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
    public int NoShowPct => (TotalConfirmed + TotalNoShow) > 0
        ? (int)Math.Round(100.0 * TotalNoShow / (TotalConfirmed + TotalNoShow), MidpointRounding.AwayFromZero)
        : 0;

    public int CompletionPct => (TotalConfirmed + TotalNoShow) > 0
        ? Math.Clamp(100 - NoShowPct, 0, 100)
        : 0;
}

internal sealed record PostEventPeriodRow(int TotalConfirmed, int TotalNoShow)
{
    public int NoShowPct => (TotalConfirmed + TotalNoShow) > 0
        ? (int)Math.Round(100.0 * TotalNoShow / (TotalConfirmed + TotalNoShow), MidpointRounding.AwayFromZero)
        : 0;

    public int CompletionPct => (TotalConfirmed + TotalNoShow) > 0
        ? Math.Clamp(100 - NoShowPct, 0, 100)
        : 0;
}
