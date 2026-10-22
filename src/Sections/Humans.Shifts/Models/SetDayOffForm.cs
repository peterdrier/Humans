using System.ComponentModel.DataAnnotations;

namespace Humans.Shifts.Models;

/// <summary>
/// The service trims and silently caps <c>Reason</c> at 200; <c>StringLength</c> rejects oversized bodies before it
/// runs.
/// </summary>
internal sealed class SetDayOffForm
{
    public Guid UserId { get; set; }
    public int DayOffset { get; set; }
    [StringLength(200)] public string? Reason { get; set; }
}

internal sealed class ClearDayOffForm
{
    public Guid UserId { get; set; }
    public int DayOffset { get; set; }
}
