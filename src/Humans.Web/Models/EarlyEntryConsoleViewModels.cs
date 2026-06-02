using System.ComponentModel.DataAnnotations;
using NodaTime;

namespace Humans.Web.Models;

/// <summary>
/// Page view model for the Art Early Entry admin console. The console targets a
/// SINGLE team — the one with early entry enabled — so there is no team picker.
/// When exactly one team is enabled, <see cref="IsConfigured"/> is true and
/// <see cref="Grants"/> + <see cref="TeamName"/> are populated; otherwise (zero
/// or several enabled) <see cref="ConfigMessage"/> explains the misconfiguration.
/// <see cref="LocalDate"/> on rows is display-only; form POSTs use the string
/// fields on the input models below — there is no MVC LocalDate binder.
/// </summary>
public sealed class EarlyEntryConsoleViewModel
{
    public IReadOnlyList<EarlyEntryConsoleRowViewModel> Grants { get; init; } = [];

    /// <summary>The target team's name (the single early-entry-enabled team).</summary>
    public string TeamName { get; init; } = string.Empty;

    /// <summary>True when exactly one team has early entry enabled.</summary>
    public bool IsConfigured { get; init; }

    /// <summary>Set when not configured (zero or multiple enabled teams).</summary>
    public string? ConfigMessage { get; init; }
}

public sealed class EarlyEntryConsoleRowViewModel
{
    public Guid GrantId { get; init; }
    public string HumanName { get; init; } = string.Empty;  // resolved via IUserServiceRead
    public LocalDate EntryDate { get; init; }               // display only — not form-bound
    public string ProjectName { get; init; } = string.Empty;
}

/// <summary>Add-grant form input. The target team is resolved server-side, so no
/// team field is bound. EntryDate is a string (yyyy-MM-dd) parsed in the
/// controller via LocalDatePattern.Iso — LocalDate does not bind from form POST.</summary>
public sealed class AddEarlyEntryConsoleInput
{
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
    [Required] public Guid GrantId { get; set; }

    [Required, RegularExpression(@"^\d{4}-\d{2}-\d{2}$")]
    public string EntryDate { get; set; } = string.Empty;

    [Required, StringLength(256)]
    public string ProjectName { get; set; } = string.Empty;
}
