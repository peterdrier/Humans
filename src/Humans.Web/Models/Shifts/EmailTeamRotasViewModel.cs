using System.ComponentModel.DataAnnotations;

namespace Humans.Web.Models.Shifts;

/// <summary>
/// Compose-form model for the coordinator "email everyone across this team's
/// upcoming rotas" action. Team-level analog of <see cref="EmailRotaViewModel"/>.
/// </summary>
public sealed class EmailTeamRotasViewModel
{
    public string TeamSlug { get; set; } = string.Empty;
    public string TeamName { get; set; } = string.Empty;
    public int RotaCount { get; set; }
    public int RecipientCount { get; set; }
    public IReadOnlyList<string> RecipientNames { get; set; } = [];

    [Required]
    [StringLength(4000, MinimumLength = 1)]
    public string Message { get; set; } = string.Empty;
}
