using System.ComponentModel.DataAnnotations;

namespace Humans.Shifts.Models;

internal sealed class EmailRotaViewModel
{
    public Guid RotaId { get; set; }
    public string RotaName { get; set; } = string.Empty;
    public string TeamSlug { get; set; } = string.Empty;
    public int RecipientCount { get; set; }
    public IReadOnlyList<string> RecipientNames { get; set; } = [];

    public bool IncludeShifts { get; set; } = true;

    [Required]
    [StringLength(4000, MinimumLength = 1)]
    public string Message { get; set; } = string.Empty;
}
