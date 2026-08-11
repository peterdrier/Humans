namespace Humans.UI.Models;

/// <summary>
/// One row of a "who is this assigned to" person dropdown: an id and the label to
/// render for it. Shared rather than per-section because it names no section's
/// vocabulary — the same shape as <see cref="HumanLookupSearchResult"/>. Feedback and
/// Issues both bind it, and Feedback's move to its own project is what took it out of
/// <c>Humans.Web.Models</c> (G5, nobodies-collective/Humans#866).
/// </summary>
public class AssigneeOption
{
    public Guid Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
}

/// <summary>
/// One row of a "filter by who reported it" dropdown: the reporter, their label, and how
/// many items they filed. Section-neutral for the same reason as
/// <see cref="AssigneeOption"/>.
/// </summary>
public class ReporterDropdownItem
{
    public Guid UserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public int Count { get; set; }
}
