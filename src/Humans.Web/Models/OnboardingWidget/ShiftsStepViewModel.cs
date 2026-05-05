namespace Humans.Web.Models.OnboardingWidget;

/// <summary>
/// Step 2 of the onboarding widget — surfaces priority shifts (or all shifts when toggled)
/// using the same browse partials as the main /Shifts page.
/// </summary>
public class ShiftsStepViewModel
{
    /// <summary>
    /// True when the user has clicked "Show all shifts" — controls whether the browse
    /// model was loaded with <c>priorityOnly: false</c>.
    /// </summary>
    public bool ShowAll { get; set; }

    /// <summary>
    /// The browse-partial model — the same <see cref="ShiftBrowseViewModel"/> the
    /// /Shifts page builds. Reused so the existing <c>_EventRotaTable</c> and
    /// <c>_BuildStrikeRotaTable</c> partials can render unmodified.
    /// </summary>
    public required ShiftBrowseViewModel BrowseModel { get; set; }
}
