namespace Humans.Web.Models.OnboardingWidget;

/// <summary>
/// Step 2 of the onboarding widget — surfaces shifts ranked by urgency,
/// filtered by a Critical / Important / All pill, with event-wide stats
/// rendered above the list ("X% of critical filled, Y important open").
/// </summary>
public class ShiftsStepViewModel
{
    /// <summary>
    /// Currently-selected pill. One of "critical", "important", "all".
    /// Default lands on "critical" so first-time users see the most-urgent
    /// shortfall first.
    /// </summary>
    public required string SelectedPriority { get; set; }

    /// <summary>
    /// Percentage of slots filled across Essential-priority shifts in the
    /// active event. Null when the event has no Essential shifts at all.
    /// </summary>
    public int? CriticalFilledPercent { get; set; }

    /// <summary>True when the event has at least one Essential-priority shift.</summary>
    public bool HasAnyCritical { get; set; }

    /// <summary>
    /// Count of Important-priority shifts (Shift entities) that still have
    /// at least one open slot — i.e. confirmed signups &lt; max volunteers.
    /// </summary>
    public int ImportantOpenCount { get; set; }

    /// <summary>True when the event has at least one Important-priority shift.</summary>
    public bool HasAnyImportant { get; set; }

    /// <summary>
    /// The browse-partial model — the same <see cref="ShiftBrowseViewModel"/> the
    /// /Shifts page builds, filtered to the rotas matching <see cref="SelectedPriority"/>.
    /// </summary>
    public required ShiftBrowseViewModel BrowseModel { get; set; }
}
