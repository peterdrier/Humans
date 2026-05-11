using Humans.Application.DTOs;
using NodaTime;

namespace Humans.Web.Models;

/// <summary>
/// View-model for <c>Views/VolunteerTracking/Index.cshtml</c>. Carries both
/// cohorts (already sorted in the controller per
/// <c>memory/architecture/display-sort-in-controllers.md</c>), the build-
/// window start offset for the column layout, and the three filter-toggle
/// states so the view can render the toggles in their current state. Display
/// names are resolved by <c>&lt;vc:human&gt;</c> at row-render time per
/// <c>memory/architecture/burnername-is-the-display-name.md</c>.
/// </summary>
public sealed class VolunteerTrackingPageViewModel
{
    public bool HasActiveEvent { get; init; }
    public int BuildStartOffset { get; init; }
    public LocalDate GateOpeningDate { get; init; }
    public LocalDate Today { get; init; }
    public IReadOnlyList<VolunteerHeatmapRow> MainCohort { get; init; } = Array.Empty<VolunteerHeatmapRow>();
    public IReadOnlyList<VolunteerCohortRow> UnbookedCohort { get; init; } = Array.Empty<VolunteerCohortRow>();

    public bool HideNoGaps { get; init; }
    public bool HideCampSetup { get; init; }
    public bool HideUnbookedSection { get; init; }

    public VolunteerTrackingPageViewModel() { }

    public VolunteerTrackingPageViewModel(
        int buildStartOffset,
        LocalDate gateOpeningDate,
        LocalDate today,
        IReadOnlyList<VolunteerHeatmapRow> mainCohort,
        IReadOnlyList<VolunteerCohortRow> unbookedCohort,
        bool hideNoGaps,
        bool hideCampSetup,
        bool hideUnbookedSection)
    {
        HasActiveEvent = true;
        BuildStartOffset = buildStartOffset;
        GateOpeningDate = gateOpeningDate;
        Today = today;
        MainCohort = mainCohort;
        UnbookedCohort = unbookedCohort;
        HideNoGaps = hideNoGaps;
        HideCampSetup = hideCampSetup;
        HideUnbookedSection = hideUnbookedSection;
    }

    public static VolunteerTrackingPageViewModel Empty { get; } = new();
}
