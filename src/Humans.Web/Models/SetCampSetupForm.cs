using System.ComponentModel.DataAnnotations;

namespace Humans.Web.Models;

/// <summary>
/// Form binding for <c>POST /Shifts/Dashboard/VolunteerTracking/SetCampSetup</c>.
/// <see cref="Date"/> is a wire-format ISO 8601 calendar date (yyyy-MM-dd),
/// kept as a string with an explicit regex; the controller parses it with
/// <c>LocalDatePattern.Iso.Parse</c> after the regex passes. Note: NodaTime
/// <c>LocalDate</c> binds fine from form input via its TypeConverter (NodaTime ≥ 3.1)
/// — the string field is a legacy workaround, not a necessity. The real form-binding
/// hazard was <c>LocalDateTime</c> posted without seconds, now handled by
/// <see cref="Infrastructure.LocalDateTimeModelBinder"/>.
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
