using System.ComponentModel.DataAnnotations;

namespace Humans.Web.Models;

/// <summary>
/// Form payload for the coord's "Mark day off" action posted from the
/// volunteer-tracking heatmap popover. <c>UserId</c> is the target volunteer
/// (the coord is the current ClaimsPrincipal). <c>DayOffset</c> is the
/// build-window offset relative to gate-open. <c>Reason</c> is optional;
/// the service trims and (silently) caps at 200 chars — the
/// <c>StringLength</c> attribute rejects oversized POST bodies before the
/// service ever runs.
/// </summary>
public sealed class SetDayOffForm
{
    public Guid UserId { get; set; }
    public int DayOffset { get; set; }
    [StringLength(200)] public string? Reason { get; set; }
}

/// <summary>
/// Form payload for the coord's "Cancel day off" action.
/// </summary>
public sealed class ClearDayOffForm
{
    public Guid UserId { get; set; }
    public int DayOffset { get; set; }
}
