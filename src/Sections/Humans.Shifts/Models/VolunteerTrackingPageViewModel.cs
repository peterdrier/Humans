using Humans.Shifts.Contracts;
using NodaTime;
using Humans.Shifts.Services.Dtos;

namespace Humans.Shifts.Models;

/// <summary>
/// Cohorts arrive sorted by the controller. <see cref="DisplayNameByUserId"/> is batch-resolved, for the row
/// tooltip, search string and sort only; rendered names come from <c>&lt;vc:human&gt;</c>.
/// </summary>
internal sealed class VolunteerTrackingPageViewModel
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

    /// <summary>Defaults to no departments so the partial renders safely when there is no active event.</summary>
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

    internal static VolunteerTrackingPageViewModel Empty { get; } = new();
}
