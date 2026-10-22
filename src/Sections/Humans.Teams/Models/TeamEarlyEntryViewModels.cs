using System.ComponentModel.DataAnnotations;
using NodaTime;

namespace Humans.Teams.Models;

internal sealed class TeamEarlyEntryPageViewModel
{
    public string Slug { get; init; } = string.Empty;

    public string TeamName { get; init; } = string.Empty;

    public IReadOnlyList<TeamEarlyEntryRowViewModel> Grants { get; init; } = [];
}

internal sealed class TeamEarlyEntryRowViewModel
{
    public Guid GrantId { get; init; }
    public string HumanName { get; init; } = string.Empty;
    public LocalDate EntryDate { get; init; }               // display only, not form-bound
    public string ProjectName { get; init; } = string.Empty;
}

/// <summary>EntryDate is a yyyy-MM-dd string parsed in the controller, for its explicit validation.</summary>
internal sealed class AddTeamEarlyEntryInput
{
    public Guid UserId { get; set; }

    [Required, RegularExpression(@"^\d{4}-\d{2}-\d{2}$")]
    public string EntryDate { get; set; } = string.Empty;

    [Required, StringLength(256)]
    public string ProjectName { get; set; } = string.Empty;
}

internal sealed class EditTeamEarlyEntryInput
{
    [Required] public Guid GrantId { get; set; }

    [Required, RegularExpression(@"^\d{4}-\d{2}-\d{2}$")]
    public string EntryDate { get; set; } = string.Empty;

    [Required, StringLength(256)]
    public string ProjectName { get; set; } = string.Empty;
}
