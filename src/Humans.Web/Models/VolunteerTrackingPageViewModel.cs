using Humans.Application.DTOs;
using Humans.Web.Models.VolunteerTracking;
using NodaTime;

namespace Humans.Web.Models;

/// <summary>
/// View-model for <c>Views/VolunteerTracking/Index.cshtml</c>. Carries both
/// cohorts (already sorted in the controller per
/// <c>memory/architecture/display-sort-in-controllers.md</c>), the build-
/// window start offset for the column layout, the three filter-toggle states,
/// and the BurnerName-resolved display name per row (batched in the
/// controller so the partials never make per-row service calls). Avatars and
/// rendered names come from <c>&lt;vc:human&gt;</c> per
/// <c>memory/architecture/burnername-is-the-display-name.md</c>; the
/// <see cref="DisplayNameByUserId"/> dictionary is only used for the row
/// tooltip / search-filter string and for the controller-side display sort.
/// </summary>
public sealed class VolunteerTrackingPageViewModel
{
    public bool HasActiveEvent { get; init; }
    public int BuildStartOffset { get; init; }
    public LocalDate GateOpeningDate { get; init; }
    public LocalDate Today { get; init; }
    public IReadOnlyList<VolunteerHeatmapRow> MainCohort { get; init; } = [];
    public IReadOnlyList<VolunteerCohortRow> UnbookedCohort { get; init; } = [];
    public IReadOnlyDictionary<Guid, string> DisplayNameByUserId { get; init; }
        = new Dictionary<Guid, string>();

    public bool HideNoGaps { get; init; }
    public bool HideCampSetup { get; init; }
    public bool HideUnbookedSection { get; init; }

    /// <summary>
    /// State for the Export card. Populated by the controller alongside the
    /// active-event branch; the empty-event short-circuit uses a default
    /// instance with no departments so the partial can render safely.
    /// </summary>
    public VolunteerTrackingExportFormViewModel ExportForm { get; init; } = new();

    public VolunteerTrackingPageViewModel() { }

    public VolunteerTrackingPageViewModel(
        int buildStartOffset,
        LocalDate gateOpeningDate,
        LocalDate today,
        IReadOnlyList<VolunteerHeatmapRow> mainCohort,
        IReadOnlyList<VolunteerCohortRow> unbookedCohort,
        IReadOnlyDictionary<Guid, string> displayNameByUserId,
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
        DisplayNameByUserId = displayNameByUserId;
        HideNoGaps = hideNoGaps;
        HideCampSetup = hideCampSetup;
        HideUnbookedSection = hideUnbookedSection;
    }

    public static VolunteerTrackingPageViewModel Empty { get; } = new();
}
