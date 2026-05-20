namespace Humans.Web.Models;

/// <summary>
/// View model for the HumanSearch ViewComponent (inline person picker).
/// </summary>
public class HumanSearchPickerViewModel
{
    /// <summary>Name of the hidden input that carries the picked user id.</summary>
    public string FieldName { get; set; } = "userId";

    /// <summary>
    /// Disambiguates element IDs when multiple pickers render on the same page.
    /// Null falls back to a random suffix.
    /// </summary>
    public string? InstanceKey { get; set; }

    /// <summary>Placeholder for the visible search box. Null uses the default.</summary>
    public string? Placeholder { get; set; }

    /// <summary>
    /// Scope hint passed through to <c>/api/profiles/search</c>. "name" narrows to
    /// display + burner name; null keeps the broad search.
    /// </summary>
    public string? Scope { get; set; }

    /// <summary>User ids to hide from the dropdown results.</summary>
    public IEnumerable<Guid>? ExcludeUserIds { get; set; }

    /// <summary>Optional prefill — the picked user id (null = empty picker).</summary>
    public Guid? SelectedUserId { get; set; }

    /// <summary>Optional prefill — the picked user's display name (BurnerName).</summary>
    public string? SelectedDisplayName { get; set; }
}
