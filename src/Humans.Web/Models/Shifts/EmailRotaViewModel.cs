using System.ComponentModel.DataAnnotations;

namespace Humans.Web.Models.Shifts;

/// <summary>
/// Compose-form model for the coordinator "email a rota" action
/// (issue nobodies-collective/Humans#732).
/// </summary>
public sealed class EmailRotaViewModel
{
    public Guid RotaId { get; set; }
    public string RotaName { get; set; } = string.Empty;
    public string TeamSlug { get; set; } = string.Empty;
    public int RecipientCount { get; set; }
    public IReadOnlyList<string> RecipientNames { get; set; } = [];

    [Required]
    [StringLength(4000, MinimumLength = 1)]
    public string Message { get; set; } = string.Empty;
}
