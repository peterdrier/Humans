using Humans.Application.DTOs;
using Humans.Domain.Entities;
using NodaTime;

namespace Humans.Web.Models;

/// <summary>
/// View-model for <c>Views/VolunteerTracking/Index.cshtml</c>. Carries both
/// cohorts (already sorted in the controller per
/// <c>memory/architecture/display-sort-in-controllers.md</c>), the user
/// dictionary needed for avatar/display-name rendering, the build-window
/// start offset for the column layout, and the three filter-toggle states
/// so the view can render the toggles in their current state.
/// </summary>
public sealed class VolunteerTrackingPageViewModel
{
    public bool HasActiveEvent { get; init; }
    public int BuildStartOffset { get; init; }
    public LocalDate GateOpeningDate { get; init; }
    public IReadOnlyList<VolunteerHeatmapRow> MainCohort { get; init; } = Array.Empty<VolunteerHeatmapRow>();
    public IReadOnlyList<VolunteerCohortRow> UnbookedCohort { get; init; } = Array.Empty<VolunteerCohortRow>();
    public IReadOnlyDictionary<Guid, User> Users { get; init; } = new Dictionary<Guid, User>();

    public bool HideNoGaps { get; init; }
    public bool HideCampSetup { get; init; }
    public bool HideUnbookedSection { get; init; }

    public VolunteerTrackingPageViewModel() { }

    public VolunteerTrackingPageViewModel(
        int buildStartOffset,
        LocalDate gateOpeningDate,
        IReadOnlyList<VolunteerHeatmapRow> mainCohort,
        IReadOnlyList<VolunteerCohortRow> unbookedCohort,
        IReadOnlyDictionary<Guid, User> users,
        bool hideNoGaps,
        bool hideCampSetup,
        bool hideUnbookedSection)
    {
        HasActiveEvent = true;
        BuildStartOffset = buildStartOffset;
        GateOpeningDate = gateOpeningDate;
        MainCohort = mainCohort;
        UnbookedCohort = unbookedCohort;
        Users = users;
        HideNoGaps = hideNoGaps;
        HideCampSetup = hideCampSetup;
        HideUnbookedSection = hideUnbookedSection;
    }

    public static VolunteerTrackingPageViewModel Empty { get; } = new();
}
