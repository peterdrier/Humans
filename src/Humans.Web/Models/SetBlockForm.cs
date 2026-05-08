using System.ComponentModel.DataAnnotations;

namespace Humans.Web.Models;

/// <summary>
/// Form binding for <c>POST /ShiftDashboard/VolunteerTracking/SetBlock</c>.
/// Coordinator path — adds or removes a single day-offset on another user's
/// blocked-days list. <see cref="Block"/> is the desired final state, not a
/// toggle: the action is idempotent.
/// </summary>
public sealed class SetBlockForm
{
    [Required] public Guid UserId { get; set; }
    [Required] public int DayOffset { get; set; }
    [Required] public bool Block { get; set; }
}
