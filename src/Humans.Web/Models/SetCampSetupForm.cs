using System.ComponentModel.DataAnnotations;

namespace Humans.Web.Models;

/// <summary>
/// Form binding for <c>POST /Shifts/Dashboard/VolunteerTracking/SetCampSetup</c>.
/// <see cref="Date"/> is a wire-format ISO 8601 calendar date (yyyy-MM-dd) —
/// NodaTime <c>LocalDate</c> cannot bind directly from form input
/// (the project does not register an MVC <c>LocalDate</c> model binder).
/// The controller parses the string with <c>LocalDatePattern.Iso.Parse</c>
/// after the regex passes.
/// </summary>
public sealed class SetCampSetupForm
{
    [Required]
    public Guid UserId { get; set; }

    [Required]
    [RegularExpression(@"^\d{4}-\d{2}-\d{2}$")]
    public string Date { get; set; } = "";

    [StringLength(500)]
    public string? Notes { get; set; }
}
