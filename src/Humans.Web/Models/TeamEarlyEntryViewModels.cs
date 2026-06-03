using System.ComponentModel.DataAnnotations;
using NodaTime;

namespace Humans.Web.Models;

/// <summary>
/// Page view model for a team's early-entry management page
/// (<c>Teams/{slug}/EarlyEntry</c>). Each team with <c>EarlyEntryEnabled</c> set
/// has its own page; multiple teams may have early entry enabled at once. The page
/// lists this team's grants and offers add/edit/remove. <see cref="LocalDate"/> on
/// rows is display-only; form POSTs use the string fields on the input models below
/// — there is no MVC LocalDate binder.
/// </summary>
public sealed class TeamEarlyEntryPageViewModel
{
    /// <summary>The team's slug, used to route the page's add/edit/remove forms.</summary>
    public string Slug { get; init; } = string.Empty;

    /// <summary>The team's name, shown in the page title and heading.</summary>
    public string TeamName { get; init; } = string.Empty;

    public IReadOnlyList<TeamEarlyEntryRowViewModel> Grants { get; init; } = [];
}

public sealed class TeamEarlyEntryRowViewModel
{
    public Guid GrantId { get; init; }
    public string HumanName { get; init; } = string.Empty;  // resolved via IUserServiceRead
    public LocalDate EntryDate { get; init; }               // display only — not form-bound
    public string ProjectName { get; init; } = string.Empty;
}

/// <summary>Add-grant form input. The target team is resolved from the route slug,
/// so no team field is bound. EntryDate is a string (yyyy-MM-dd) parsed in the
/// controller via LocalDatePattern.Iso — LocalDate does not bind from form POST.
/// The empty-GUID guard for UserId lives at the service boundary.</summary>
public sealed class AddTeamEarlyEntryInput
{
    public Guid UserId { get; set; }

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
