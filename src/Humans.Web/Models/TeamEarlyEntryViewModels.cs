using System.ComponentModel.DataAnnotations;
using NodaTime;

namespace Humans.Web.Models;

/// <summary>
/// Page view model for the team early-entry management page. The grant rows hold a
/// resolved human display name; <see cref="LocalDate"/> is display-only (form POSTs
/// use the string fields on the input models below — there is no MVC LocalDate binder).
/// </summary>
public sealed class TeamEarlyEntryPageViewModel
{
    public Guid TeamId { get; init; }
    public string TeamName { get; init; } = string.Empty;
    public string Slug { get; init; } = string.Empty;
    public IReadOnlyList<TeamEarlyEntryRowViewModel> Grants { get; init; } = [];
}

public sealed class TeamEarlyEntryRowViewModel
{
    public Guid GrantId { get; init; }
    public Guid UserId { get; init; }
    public string HumanName { get; init; } = string.Empty;  // resolved via IUserServiceRead
    public LocalDate EntryDate { get; init; }               // display only — not form-bound
    public string ProjectName { get; init; } = string.Empty;
}

/// <summary>Add-grant form input. EntryDate is a string (yyyy-MM-dd) parsed in the
/// controller via LocalDatePattern.Iso — LocalDate does not bind from form POST.</summary>
public sealed class AddTeamEarlyEntryInput
{
    [Required] public Guid UserId { get; set; }

    [Required, RegularExpression(@"^\d{4}-\d{2}-\d{2}$")]
    public string EntryDate { get; set; } = string.Empty;

    [Required, StringLength(256)]
    public string ProjectName { get; set; } = string.Empty;
}

/// <summary>Edit-grant form input. See <see cref="AddTeamEarlyEntryInput"/> for the
/// string-EntryDate rationale.</summary>
public sealed class EditTeamEarlyEntryInput
{
    [Required] public Guid GrantId { get; set; }

    [Required, RegularExpression(@"^\d{4}-\d{2}-\d{2}$")]
    public string EntryDate { get; set; } = string.Empty;

    [Required, StringLength(256)]
    public string ProjectName { get; set; } = string.Empty;
}
