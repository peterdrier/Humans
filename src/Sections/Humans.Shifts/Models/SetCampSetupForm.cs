using System.ComponentModel.DataAnnotations;

namespace Humans.Shifts.Models;

/// <summary>
/// <see cref="Date"/> stays a regex-checked ISO string; the controller parses it after the regex passes.
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
