using Humans.Shifts.Contracts;

namespace Humans.Onboarding.Models;

/// <summary>
/// Builds the <see cref="ShiftsStepViewModel"/> for the onboarding widget's step-2 view
/// from the full set of <see cref="UrgentShiftInfo"/> entries for the active event. Computes
/// event-wide stats (critical fill %, important open count) from the unfiltered list,
/// then filters to the rotas matching the user's pill selection for display.
///
/// Lives outside the controller so the action stays under the
/// no-business-logic-in-controllers ratchet thresholds.
/// </summary>
/// <remarks>
/// This is the half of Shell's former <c>OnboardingShiftsBrowseModelBuilder</c> that names
/// only <c>Humans.Domain</c> and <c>Humans.Application</c> types. The other half — grouping
/// the filtered shifts into <c>RotaShiftGroup</c>s through <c>ShiftBrowseMapper</c> — stayed
/// in Shell with the rest of Shifts' presentation layer and is now
/// <c>OnboardingShiftsListViewComponent</c>.
/// </remarks>
internal static class OnboardingShiftsStepBuilder
{
    internal const string PriorityCritical = "critical";
    internal const string PriorityImportant = "important";
    internal const string PriorityAll = "all";

    internal static ShiftsStepViewModel Build(
        BurnSettingsInfo eventSettings,
        IReadOnlyList<UrgentShiftInfo> allShifts,
        HashSet<Guid> userSignupShiftIds,
        Dictionary<Guid, SignupStatus> userSignupStatuses,
        string selectedPriority,
        bool earlyEntrySignupsClosed = false)
    {
        var normalizedPriority = NormalizePriority(selectedPriority);
        var stats = ComputeStats(allShifts);

        return new ShiftsStepViewModel
        {
            SelectedPriority = normalizedPriority,
            CriticalFilledPercent = stats.CriticalFilledPercent,
            HasAnyCritical = stats.HasAnyCritical,
            ImportantOpenCount = stats.ImportantOpenCount,
            HasAnyImportant = stats.HasAnyImportant,
            EventSettings = eventSettings,
            Shifts = FilterByPriority(allShifts, normalizedPriority).ToList(),
            UserSignupShiftIds = userSignupShiftIds,
            UserSignupStatuses = userSignupStatuses,
            EarlyEntrySignupsClosed = earlyEntrySignupsClosed,
        };
    }

    internal static ShiftsStepViewModel BuildEmpty(string selectedPriority) =>
        new()
        {
            SelectedPriority = NormalizePriority(selectedPriority),
            EventSettings = null,
        };

    internal static string NormalizePriority(string? value) =>
        value switch
        {
            PriorityImportant => PriorityImportant,
            PriorityAll => PriorityAll,
            _ => PriorityCritical,
        };

    private static IEnumerable<UrgentShiftInfo> FilterByPriority(
        IReadOnlyList<UrgentShiftInfo> all, string normalizedPriority) =>
        normalizedPriority switch
        {
            PriorityCritical => all.Where(u => u.Rota.Priority == ShiftPriority.Essential),
            PriorityImportant => all.Where(u => u.Rota.Priority == ShiftPriority.Important),
            _ => all,
        };

    private sealed record StatsSnapshot(
        int? CriticalFilledPercent,
        bool HasAnyCritical,
        int ImportantOpenCount,
        bool HasAnyImportant);

    private static StatsSnapshot ComputeStats(IReadOnlyList<UrgentShiftInfo> all)
    {
        var critical = all.Where(u => u.Rota.Priority == ShiftPriority.Essential).ToList();
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

        var important = all.Where(u => u.Rota.Priority == ShiftPriority.Important).ToList();
        var importantOpenCount = important.Count(u => u.RemainingSlots > 0);

        return new StatsSnapshot(
            criticalFilledPercent,
            hasAnyCritical,
            importantOpenCount,
            HasAnyImportant: important.Count > 0);
    }
}
