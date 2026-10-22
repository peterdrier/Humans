namespace Humans.Base.Models;

/// <summary>
/// One row of a "who is this assigned to" person dropdown. Shared because it names no section's vocabulary:
/// Feedback and Issues both bind it.
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
