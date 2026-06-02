using System.ComponentModel.DataAnnotations;
using NodaTime;

namespace Humans.Web.Models;

/// <summary>
/// Page view model for the global Art Early Entry admin console. Rows span every
/// early-entry-enabled team (each row carries its owning <see cref="EarlyEntryConsoleRowViewModel.TeamId"/>
/// so edit/remove can be scoped). <see cref="LocalDate"/> is display-only; form POSTs
/// use the string fields on the input models below — there is no MVC LocalDate binder.
/// </summary>
public sealed class EarlyEntryConsoleViewModel
{
    public IReadOnlyList<EarlyEntryConsoleRowViewModel> Grants { get; init; } = [];

    /// <summary>Early-entry-enabled teams, ordered by name — the Add-form team dropdown.</summary>
    public IReadOnlyList<EarlyEntryTeamOption> Teams { get; init; } = [];
}

public sealed class EarlyEntryConsoleRowViewModel
{
    public Guid GrantId { get; init; }
    public Guid TeamId { get; init; }
    public string TeamName { get; init; } = string.Empty;   // resolved via ITeamServiceRead
    public string HumanName { get; init; } = string.Empty;  // resolved via IUserServiceRead
    public LocalDate EntryDate { get; init; }               // display only — not form-bound
    public string ProjectName { get; init; } = string.Empty;
}

/// <summary>One option in the Add-form team dropdown (value = Id, text = Name).</summary>
public sealed class EarlyEntryTeamOption
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
}

/// <summary>Add-grant form input. EntryDate is a string (yyyy-MM-dd) parsed in the
/// controller via LocalDatePattern.Iso — LocalDate does not bind from form POST.</summary>
public sealed class AddEarlyEntryConsoleInput
{
    [Required] public Guid TeamId { get; set; }
    [Required] public Guid UserId { get; set; }

    [Required, RegularExpression(@"^\d{4}-\d{2}-\d{2}$")]
    public string EntryDate { get; set; } = string.Empty;

    [Required, StringLength(256)]
    public string ProjectName { get; set; } = string.Empty;
}

/// <summary>Edit-grant form input. See <see cref="AddEarlyEntryConsoleInput"/> for the
/// string-EntryDate rationale.</summary>
public sealed class EditEarlyEntryConsoleInput
{
    [Required] public Guid TeamId { get; set; }
    [Required] public Guid GrantId { get; set; }

    [Required, RegularExpression(@"^\d{4}-\d{2}-\d{2}$")]
    public string EntryDate { get; set; } = string.Empty;

    [Required, StringLength(256)]
    public string ProjectName { get; set; } = string.Empty;
}
