using System.ComponentModel.DataAnnotations;

namespace Humans.Shifts.Models;

/// <summary>
/// Compose-form model for the coordinator "email a rota" action.
/// </summary>
internal sealed class EmailRotaViewModel
{
    public Guid RotaId { get; set; }
    public string RotaName { get; set; } = string.Empty;
    public string TeamSlug { get; set; } = string.Empty;
    public int RecipientCount { get; set; }
    public IReadOnlyList<string> RecipientNames { get; set; } = [];

    /// <summary>
    /// Whether each recipient's own shifts on this rota are listed in their email.
    /// On by default; cleared for messages where the schedule is noise (a thank-you).
    /// </summary>
    public bool IncludeShifts { get; set; } = true;

    [Required]
    [StringLength(4000, MinimumLength = 1)]
    public string Message { get; set; } = string.Empty;
}
