using System.ComponentModel.DataAnnotations;

namespace Humans.Shifts.Models;

/// <summary>
/// Form binding for <c>POST /Shifts/Dashboard/VolunteerTracking/SetCampSetup</c>.
/// <see cref="Date"/> is an ISO 8601 calendar date (yyyy-MM-dd) kept as a string
/// with an explicit regex; the controller parses it with
/// <c>LocalDatePattern.Iso.Parse</c> after the regex passes.
/// </summary>
internal sealed class SetCampSetupForm
{
    [Required]
    public Guid UserId { get; set; }

    [Required]
    [RegularExpression(@"^\d{4}-\d{2}-\d{2}$")]
    public string Date { get; set; } = "";

    [StringLength(500)]
    public string? Notes { get; set; }
}
