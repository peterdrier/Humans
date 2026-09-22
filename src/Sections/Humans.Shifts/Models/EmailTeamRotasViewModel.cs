using System.ComponentModel.DataAnnotations;
using Humans.Shifts.Services;

namespace Humans.Shifts.Models;

/// <summary>
/// Compose-form model for the coordinator "email everyone across this team's
/// rotas" action. Team-level analog of <see cref="EmailRotaViewModel"/>.
/// </summary>
internal sealed class EmailTeamRotasViewModel
{
    /// <summary>
    /// Submit value that re-renders the form against the current audience selection
    /// instead of sending. Posted by the audience controls, not the send button.
    /// </summary>
    public const string RefreshIntent = "refresh";

    public string TeamSlug { get; set; } = string.Empty;
    public string TeamName { get; set; } = string.Empty;
    public int RotaCount { get; set; }
    public int RecipientCount { get; set; }
    public IReadOnlyList<string> RecipientNames { get; set; } = [];

    /// <summary>
    /// Whether each recipient's own shifts are listed in their email. On by default.
    /// </summary>
    public bool IncludeShifts { get; set; } = true;

    /// <summary>
    /// True (the default) narrows the audience to rotas with a shift that has not yet
    /// ended. False opens the whole active event, and the period flags below then apply.
    /// </summary>
    public bool UpcomingOnly { get; set; } = true;

    public bool IncludeBuild { get; set; } = true;

    public bool IncludeEvent { get; set; } = true;

    public bool IncludeStrike { get; set; } = true;

    /// <summary>The audience these choices describe.</summary>
    public TeamRotasAudienceFilter Filter =>
        new(UpcomingOnly, IncludeBuild, IncludeEvent, IncludeStrike);

    [Required]
    [StringLength(4000, MinimumLength = 1)]
    public string Message { get; set; } = string.Empty;
}
