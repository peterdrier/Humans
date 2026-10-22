using System.ComponentModel.DataAnnotations;
using Humans.Shifts.Services;

namespace Humans.Shifts.Models;

internal sealed class EmailTeamRotasViewModel
{
    /// <summary>Posted by the audience controls: re-renders against the new audience instead of sending.</summary>
    public const string RefreshIntent = "refresh";

    public string TeamSlug { get; set; } = string.Empty;
    public string TeamName { get; set; } = string.Empty;
    public int RotaCount { get; set; }
    public int RecipientCount { get; set; }
    public IReadOnlyList<string> RecipientNames { get; set; } = [];

    public bool IncludeShifts { get; set; } = true;

    /// <summary>Rotas with a shift not yet ended; false opens the whole event, and the period flags apply.</summary>
    public bool UpcomingOnly { get; set; } = true;

    public bool IncludeBuild { get; set; } = true;

    public bool IncludeEvent { get; set; } = true;

    public bool IncludeStrike { get; set; } = true;

    public TeamRotasAudienceFilter Filter =>
        new(UpcomingOnly, IncludeBuild, IncludeEvent, IncludeStrike);

    /// <summary>
    /// Posted back with the send, so a selection changed since the preview (script off or failed) re-previews
    /// instead of mailing an audience the coordinator was never shown.
    /// </summary>
    public string PreviewedAudience { get; set; } = TeamRotasAudienceFilter.Default.Key;

    public bool AudienceChanged { get; set; }

    [Required]
    [StringLength(4000, MinimumLength = 1)]
    public string Message { get; set; } = string.Empty;
}
