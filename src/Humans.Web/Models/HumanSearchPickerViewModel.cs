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
    /// Search scope passed through to <c>/api/profiles/search</c>.
    /// <see cref="HumanSearchScope.Name"/> narrows to burner-name match;
    /// <see cref="HumanSearchScope.All"/> (default) keeps the broad search.
    /// </summary>
    public HumanSearchScope Scope { get; set; } = HumanSearchScope.All;

    /// <summary>User ids to hide from the dropdown results.</summary>
    public IEnumerable<Guid>? ExcludeUserIds { get; set; }

    /// <summary>Optional prefill — the picked user id (null = empty picker).</summary>
    public Guid? SelectedUserId { get; set; }

    /// <summary>
    /// Resolved BurnerName for the prefilled user, used to seed the visible search
    /// box. Populated by the component from <see cref="SelectedUserId"/>; the
    /// <c>internal</c> setter makes it impossible for callers to supply a name.
    /// </summary>
    public string? SelectedBurnerName { get; internal set; }
}
