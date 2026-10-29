using Humans.Cantina.Contracts;
using Humans.Cantina.Domain;
using Humans.Cantina.Services.Dtos;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Cantina.Services;

internal static class CantinaRosterCalculation
{
    internal const int DaysPerWeek = 7;

    // Must match the last entry of Roster.cshtml's dietaryOrder; the service tests pin the literal.
    private static readonly string UnansweredKey = "Unanswered";

    public static Dictionary<Guid, List<LocalDate>> BuildDaysOnSiteByUserId(
        IReadOnlyList<(int DayOffset, IReadOnlyList<Guid> UserIds)> perDay,
        int weekStartOffset,
        LocalDate? weekStartDate)
    {
        var daysOnSiteByUserId = new Dictionary<Guid, List<LocalDate>>();
        foreach (var (dayOffset, userIds) in perDay)
        {
            var calendarDate = weekStartDate?.PlusDays(dayOffset - weekStartOffset);
            foreach (var id in userIds)
            {
                if (!daysOnSiteByUserId.TryGetValue(id, out var list))
                {
                    list = new List<LocalDate>(capacity: DaysPerWeek);
                    daysOnSiteByUserId[id] = list;
                }

                if (calendarDate is not null)
                    list.Add(calendarDate.Value);
            }
        }

        return daysOnSiteByUserId;
    }

    public static List<DayRosterSummaryDto> BuildDaySummaries(
        IReadOnlyDictionary<Guid, List<LocalDate>> daysOnSiteByUserId,
        int weekStartOffset,
        LocalDate? weekStartDate,
        IReadOnlyDictionary<Guid, DietaryProfileInfo> dietById)
    {
        var days = new List<DayRosterSummaryDto>(DaysPerWeek);
        for (var i = 0; i < DaysPerWeek; i++)
        {
            var calendarDate = weekStartDate?.PlusDays(i);

            // Post-injection map, so arrival-day people count. Without an anchor date the map holds no dates.
            var total = 0;
            var unanswered = 0;
            if (calendarDate is { } day)
            {
                foreach (var (id, dates) in daysOnSiteByUserId)
                {
                    if (!dates.Contains(day))
                        continue;

                    total++;
                    if (!dietById.TryGetValue(id, out var diet) || !diet.HasPreference)
                        unanswered++;
                }
            }

            days.Add(new DayRosterSummaryDto(
                DayOffset: weekStartOffset + i,
                CalendarDate: calendarDate,
                TotalOnSite: total,
                UnansweredOnDay: unanswered));
        }

        return days;
    }

    public static List<DietaryProfileInfo> BuildUniqueProfiles(
        IReadOnlyList<Guid> uniqueUserIds,
        IReadOnlyDictionary<Guid, DietaryProfileInfo> dietById)
    {
        var uniqueProfiles = new List<DietaryProfileInfo>(uniqueUserIds.Count);
        foreach (var id in uniqueUserIds)
        {
            if (dietById.TryGetValue(id, out var diet))
                uniqueProfiles.Add(diet);
        }

        return uniqueProfiles;
    }

    public static LocalDate[] BuildWeekDays(LocalDate? weekStartDate)
        => weekStartDate is null
            ? Array.Empty<LocalDate>()
            : Enumerable.Range(0, DaysPerWeek)
                .Select(i => weekStartDate.Value.PlusDays(i))
                .ToArray();

    public static List<RosterPersonDto> BuildWeeklyPeople(
        IReadOnlyList<Guid> uniqueUserIds,
        IReadOnlyDictionary<Guid, UserInfo> userInfoById,
        IReadOnlyDictionary<Guid, DietaryProfileInfo> dietById,
        IReadOnlyDictionary<Guid, List<LocalDate>> daysOnSiteByUserId,
        IReadOnlyDictionary<Guid, LocalDate> arrivesOnByUser,
        IReadOnlyList<LocalDate> weekDays)
    {
        var people = new List<RosterPersonDto>(uniqueUserIds.Count);
        foreach (var id in uniqueUserIds)
        {
            userInfoById.TryGetValue(id, out var info);
            dietById.TryGetValue(id, out var profile);
            var daysList = daysOnSiteByUserId[id];
            daysList.Sort();

            people.Add(new RosterPersonDto(
                UserId: id,
                BurnerName: ResolveBurnerName(info),
                ArrivesOn: arrivesOnByUser.GetValueOrDefault(id, daysList[0]),
                NoShift: BuildNoShiftDays(daysList, weekDays),
                DietaryPreference: profile?.DietaryPreference,
                Allergies: profile?.Allergies ?? [],
                AllergyOtherText: profile?.AllergyOtherText,
                Intolerances: profile?.Intolerances ?? [],
                IntoleranceOtherText: profile?.IntoleranceOtherText));
        }

        return people;
    }

    private static IReadOnlyList<LocalDate> BuildNoShiftDays(
        IReadOnlyCollection<LocalDate> daysOnSite,
        IReadOnlyCollection<LocalDate> weekDays)
    {
        if (weekDays.Count == 0)
            return Array.Empty<LocalDate>();

        var onSiteSet = new HashSet<LocalDate>(daysOnSite);
        var noShift = new List<LocalDate>(DaysPerWeek);
        foreach (var day in weekDays)
        {
            if (!onSiteSet.Contains(day))
                noShift.Add(day);
        }

        return noShift;
    }

    /// <summary>
    /// The name Users already resolved; <c>ProfileInfo.BurnerName</c> would show "(unknown)" for a human whose name lives
    /// only on the user row. The fallback covers a user id the cohort read did not return.
    /// </summary>
    public static string ResolveBurnerName(UserInfo? info) =>
        info is not null && !string.IsNullOrWhiteSpace(info.BurnerName)
            ? info.BurnerName
            : "(unknown)";

    public static IReadOnlyDictionary<string, int> BuildDietaryBreakdown(
        IReadOnlyList<DietaryProfileInfo> uniqueProfiles, int totalUniqueOnSite)
    {
        var dict = new Dictionary<string, int>(DietaryOptions.DietaryPreferences.Count + 1, StringComparer.Ordinal);
        foreach (var pref in DietaryOptions.DietaryPreferences)
            dict[pref] = 0;

        var answered = 0;
        foreach (var profile in uniqueProfiles)
        {
            if (string.IsNullOrEmpty(profile.DietaryPreference))
                continue;

            answered++;
            // An unknown value counts as answered but matches no bucket, so the counts sum to less than
            // totalUniqueOnSite. Which bucket it belongs in is undecided: nobodies-collective/Humans#1113.
            if (dict.ContainsKey(profile.DietaryPreference))
                dict[profile.DietaryPreference]++;
        }

        dict[UnansweredKey] = totalUniqueOnSite - answered;
        return dict;
    }

    public static (IReadOnlyList<RollupItemDto> Rollup, IReadOnlyList<string> OtherEntries) BuildRollup(
        IReadOnlyList<DietaryProfileInfo> uniqueProfiles,
        Func<DietaryProfileInfo, IReadOnlyList<string>> pickChips,
        Func<DietaryProfileInfo, string?> pickOtherText,
        IReadOnlyList<string> canonicalLabels)
    {
        var counts = new Dictionary<string, int>(canonicalLabels.Count, StringComparer.Ordinal);
        foreach (var label in canonicalLabels)
            counts[label] = 0;

        // Dedup by trimmed value across the week.
        var otherSet = new HashSet<string>(StringComparer.Ordinal);
        var otherEntries = new List<string>();

        foreach (var profile in uniqueProfiles)
        {
            var chips = pickChips(profile);
            if (chips is null) continue;

            foreach (var chip in chips)
            {
                if (counts.ContainsKey(chip))
                    counts[chip]++;
            }

            if (chips.Contains(DietaryOptions.OtherOption, StringComparer.Ordinal))
            {
                var text = pickOtherText(profile);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    var trimmed = text.Trim();
                    if (otherSet.Add(trimmed))
                        otherEntries.Add(trimmed);
                }
            }
        }

        var rollup = new List<RollupItemDto>(canonicalLabels.Count);
        foreach (var label in canonicalLabels)
            rollup.Add(new RollupItemDto(label, counts[label]));

        return (rollup, otherEntries);
    }
}
