using Humans.Application.Interfaces.Shifts;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Models.Shifts;

namespace Humans.Web.Models.OnboardingWidget;

/// <summary>
/// Builds the <see cref="ShiftsStepViewModel"/> for the onboarding widget's
/// step-2 view from the full set of <see cref="UrgentShift"/> entries for the
/// active event. Computes event-wide stats (critical fill %, important open
/// count) from the unfiltered list, then filters to the rotas matching the
/// user's pill selection for display.
///
/// Lives outside the controller so the action stays under the
/// no-business-logic-in-controllers ratchet thresholds. Per-shift and per-rota
/// mapping is shared with the full browse via <see cref="ShiftBrowseMapper"/>.
/// </summary>
public static class OnboardingShiftsBrowseModelBuilder
{
    public const string PriorityCritical = "critical";
    public const string PriorityImportant = "important";
    public const string PriorityAll = "all";

    public static ShiftsStepViewModel Build(
        EventSettings eventSettings,
        IReadOnlyList<UrgentShift> allShifts,
        HashSet<Guid> userSignupShiftIds,
        Dictionary<Guid, SignupStatus> userSignupStatuses,
        string selectedPriority)
    {
        var normalizedPriority = NormalizePriority(selectedPriority);

        var stats = ComputeStats(allShifts);
        var filtered = FilterByPriority(allShifts, normalizedPriority);

        var rotaGroups = filtered
            .GroupBy(u => u.Shift.RotaId)
            .Select(rg => ShiftBrowseMapper.BuildRotaGroup(
                rg,
                eventSettings,
                departmentName: rg.First().DepartmentName))
            .OrderByDescending(r => r.MaxUrgencyScore)
            .ToList();

        var browse = new ShiftBrowseViewModel
        {
            EventSettings = eventSettings,
            ShowSignups = true,
            Sort = "urgency",
            UrgencyRankedRotas = rotaGroups,
            UserSignupShiftIds = userSignupShiftIds,
            UserSignupStatuses = userSignupStatuses,
        };

        return new ShiftsStepViewModel
        {
            SelectedPriority = normalizedPriority,
            CriticalFilledPercent = stats.CriticalFilledPercent,
            HasAnyCritical = stats.HasAnyCritical,
            ImportantOpenCount = stats.ImportantOpenCount,
            HasAnyImportant = stats.HasAnyImportant,
            BrowseModel = browse,
        };
    }

    public static ShiftsStepViewModel BuildEmpty(string selectedPriority)
    {
        return new ShiftsStepViewModel
        {
            SelectedPriority = NormalizePriority(selectedPriority),
            BrowseModel = new ShiftBrowseViewModel
            {
                EventSettings = null!,
                ShowSignups = true,
                Sort = "urgency",
            },
        };
    }

    internal static string NormalizePriority(string? value) =>
        value switch
        {
            PriorityImportant => PriorityImportant,
            PriorityAll => PriorityAll,
            _ => PriorityCritical,
        };

    private static IEnumerable<UrgentShift> FilterByPriority(
        IReadOnlyList<UrgentShift> all, string normalizedPriority) =>
        normalizedPriority switch
        {
            PriorityCritical => all.Where(u => u.Shift.Rota.Priority == ShiftPriority.Essential),
            PriorityImportant => all.Where(u => u.Shift.Rota.Priority == ShiftPriority.Important),
            _ => all,
        };

    private sealed record StatsSnapshot(
        int? CriticalFilledPercent,
        bool HasAnyCritical,
        int ImportantOpenCount,
        bool HasAnyImportant);

    private static StatsSnapshot ComputeStats(IReadOnlyList<UrgentShift> all)
    {
        var critical = all.Where(u => u.Shift.Rota.Priority == ShiftPriority.Essential).ToList();
        var hasAnyCritical = critical.Count > 0;
        int? criticalFilledPercent = null;
        if (hasAnyCritical)
        {
            var totalSlots = critical.Sum(u => u.Shift.MaxVolunteers);
            var confirmed = critical.Sum(u => u.ConfirmedCount);
            criticalFilledPercent = totalSlots > 0
                ? (int)Math.Round(100.0 * confirmed / totalSlots)
                : 0;
        }

        var important = all.Where(u => u.Shift.Rota.Priority == ShiftPriority.Important).ToList();
        var importantOpenCount = important.Count(u => u.RemainingSlots > 0);

        return new StatsSnapshot(
            criticalFilledPercent,
            hasAnyCritical,
            importantOpenCount,
            HasAnyImportant: important.Count > 0);
    }
}
