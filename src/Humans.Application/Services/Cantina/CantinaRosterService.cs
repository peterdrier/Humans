using Humans.Application;
using Humans.Application.Interfaces.Cantina;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Cantina.Dtos;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using NodaTime;

namespace Humans.Application.Services.Cantina;

/// <summary>
/// Application-layer implementation of <see cref="ICantinaRosterService"/>.
/// The on-site cohort (who is around each day) comes from
/// <see cref="IShiftManagementService.GetOnSiteUserIdsForDayAsync"/>; dietary
/// data (preference, allergies, intolerances) is read from the cross-section
/// <see cref="IUserServiceRead"/> (cached <see cref="UserInfo"/>/<see cref="ProfileInfo"/>),
/// since dietary moved to <c>Profile</c>. The service unions the days into a
/// unique-humans cohort and computes the weekly aggregates the Cantina UI needs.
/// <c>MedicalConditions</c> is never read here — the cantina plans around food, not medical history.
/// </summary>
public sealed class CantinaRosterService : ICantinaRosterService
{
    private const int DaysPerWeek = 7;

    private readonly IShiftManagementService _shiftMgmt;
    private readonly IUserServiceRead _userRead;
    private readonly IClock _clock;

    // Canonical preference labels, with the "Unanswered" pseudo-bucket.
    private static readonly string UnansweredKey = "Unanswered";

    public CantinaRosterService(
        IShiftManagementService shiftMgmt,
        IUserServiceRead userRead,
        IClock clock)
    {
        _shiftMgmt = shiftMgmt;
        _userRead = userRead;
        _clock = clock;
    }

    public async Task<WeeklyRosterDto> GetWeeklyRosterAsync(int weekStartOffset, CancellationToken ct = default)
    {
        var eventSettings = await _shiftMgmt.GetActiveAsync().ConfigureAwait(false);
        var weekStartDate = eventSettings is null
            ? (LocalDate?)null
            : eventSettings.GateOpeningDate.PlusDays(weekStartOffset);
        var weekEndDate = weekStartDate?.PlusDays(DaysPerWeek - 1);
        var eventName = eventSettings?.EventName;

        // "Today" must be computed in the event timezone — the view uses this
        // to highlight the current row in the per-day mini-table. Falling back
        // to view-side DateTime.UtcNow caused a Madrid coordinator to see
        // tomorrow highlighted late in the evening (CET is ahead of UTC).
        LocalDate? eventTodayDate = null;
        if (eventSettings is not null)
        {
            var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(eventSettings.TimeZoneId)
                ?? DateTimeZoneProviders.Tzdb["Europe/Madrid"];
            eventTodayDate = _clock.GetCurrentInstant().InZone(zone).Date;
        }

        // Per-day cohort: 7 sequential queries for the on-site user ids. At ~500
        // users this is fine (see CLAUDE.md scale notes).
        var perDay = new List<(int DayOffset, IReadOnlyList<Guid> UserIds)>(DaysPerWeek);
        for (var i = 0; i < DaysPerWeek; i++)
        {
            var dayOffset = weekStartOffset + i;
            var userIds = await _shiftMgmt.GetOnSiteUserIdsForDayAsync(dayOffset, ct).ConfigureAwait(false);
            perDay.Add((dayOffset, userIds));
        }

        // Build the union of unique on-site user IDs across the week, and
        // map each user id to the set of calendar dates they were on-site.
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

        var uniqueUserIds = daysOnSiteByUserId.Keys.ToList();

        // Dietary lives on Profile — read it from the cached UserInfo for the whole
        // on-site cohort in one batched, cache-friendly call. profileByUserId is the
        // single source for dietary preference / allergies / intolerances below.
        var profileByUserId = uniqueUserIds.Count == 0
            ? new Dictionary<Guid, ProfileInfo>()
            : BuildProfileMap(await _userRead.GetUserInfosAsync(uniqueUserIds, ct).ConfigureAwait(false));

        // Build per-day summaries (counts only). Dietary is per-user, so a day's
        // "unanswered" count is over that day's user ids.
        var days = new List<DayRosterSummaryDto>(DaysPerWeek);
        for (var i = 0; i < DaysPerWeek; i++)
        {
            var (dayOffset, userIds) = perDay[i];
            var unanswered = 0;
            foreach (var id in userIds)
            {
                if (!profileByUserId.TryGetValue(id, out var p) || string.IsNullOrEmpty(p.DietaryPreference))
                    unanswered++;
            }
            days.Add(new DayRosterSummaryDto(
                DayOffset: dayOffset,
                CalendarDate: weekStartDate?.PlusDays(i),
                TotalOnSite: userIds.Count,
                UnansweredOnDay: unanswered));
        }

        if (uniqueUserIds.Count == 0)
        {
            return new WeeklyRosterDto(
                WeekStartOffset: weekStartOffset,
                WeekStartDate: weekStartDate,
                WeekEndDate: weekEndDate,
                EventName: eventName,
                TotalUniqueOnSite: 0,
                UnansweredCount: 0,
                DietaryBreakdown: EmptyDietaryBreakdown(),
                AllergyRollup: EmptyRollup(DietaryOptions.AllergyOptions),
                AllergyOtherEntries: Array.Empty<string>(),
                IntoleranceRollup: EmptyRollup(DietaryOptions.IntoleranceOptions),
                IntoleranceOtherEntries: Array.Empty<string>(),
                Days: days,
                People: Array.Empty<RosterPersonDto>(),
                EventTodayDate: eventTodayDate);
        }

        // Unique-humans dietary cohort — every aggregate below is computed over
        // this list so a person on-site multiple days contributes exactly once.
        var uniqueProfiles = new List<ProfileInfo>(uniqueUserIds.Count);
        foreach (var id in uniqueUserIds)
        {
            if (profileByUserId.TryGetValue(id, out var p))
                uniqueProfiles.Add(p);
        }

        // The 7 calendar dates of the week, used to compute NoShift as the
        // complement of each person's on-site days. Empty when the week
        // has no anchor date (no active event) — in that branch we don't
        // reach this code path anyway since uniqueUserIds.Count == 0.
        var weekDays = weekStartDate is null
            ? Array.Empty<LocalDate>()
            : Enumerable.Range(0, DaysPerWeek)
                .Select(i => weekStartDate.Value.PlusDays(i))
                .ToArray();

        // People are returned in unspecified order. Display sort happens at
        // the Web layer in CantinaRosterAssembler (see
        // memory/architecture/display-sort-in-controllers.md).
        var people = new List<RosterPersonDto>(uniqueUserIds.Count);
        foreach (var id in uniqueUserIds)
        {
            profileByUserId.TryGetValue(id, out var profile);
            var daysList = daysOnSiteByUserId[id];
            daysList.Sort();

            // ArrivesOn is non-nullable by cohort invariant: every user
            // in daysOnSiteByUserId got there by appearing on at least
            // one day. If weekStartDate is null we never enter this
            // branch (early-return above), so daysList is non-empty.
            var arrivesOn = daysList[0];

            // NoShift = weekDays \ on-site days. HashSet for O(1) lookup.
            IReadOnlyList<LocalDate> noShift;
            if (weekDays.Length == 0)
            {
                noShift = Array.Empty<LocalDate>();
            }
            else
            {
                var onSiteSet = new HashSet<LocalDate>(daysList);
                var offList = new List<LocalDate>(DaysPerWeek);
                foreach (var day in weekDays)
                {
                    if (!onSiteSet.Contains(day))
                        offList.Add(day);
                }
                noShift = offList;
            }

            people.Add(new RosterPersonDto(
                UserId: id,
                BurnerName: ResolveBurnerName(profile),
                ArrivesOn: arrivesOn,
                NoShift: noShift,
                DietaryPreference: profile?.DietaryPreference,
                Allergies: profile?.Allergies is { Count: > 0 } a ? a.ToArray() : Array.Empty<string>(),
                AllergyOtherText: profile?.AllergyOtherText,
                Intolerances: profile?.Intolerances is { Count: > 0 } i ? i.ToArray() : Array.Empty<string>(),
                IntoleranceOtherText: profile?.IntoleranceOtherText));
        }

        var dietaryBreakdown = BuildDietaryBreakdown(uniqueProfiles, uniqueUserIds.Count);
        var (allergyRollup, allergyOther) = BuildRollup(
            uniqueProfiles,
            p => p.Allergies,
            p => p.AllergyOtherText,
            DietaryOptions.AllergyOptions);
        var (intoleranceRollup, intoleranceOther) = BuildRollup(
            uniqueProfiles,
            p => p.Intolerances,
            p => p.IntoleranceOtherText,
            DietaryOptions.IntoleranceOptions);

        // "Unanswered" = unique on-site humans whose DietaryPreference is
        // null/empty (no profile, or a profile with blank DietaryPreference).
        var answeredCount = uniqueProfiles.Count(p => !string.IsNullOrEmpty(p.DietaryPreference));
        var unansweredTotal = uniqueUserIds.Count - answeredCount;

        return new WeeklyRosterDto(
            WeekStartOffset: weekStartOffset,
            WeekStartDate: weekStartDate,
            WeekEndDate: weekEndDate,
            EventName: eventName,
            TotalUniqueOnSite: uniqueUserIds.Count,
            UnansweredCount: unansweredTotal,
            DietaryBreakdown: dietaryBreakdown,
            AllergyRollup: allergyRollup,
            AllergyOtherEntries: allergyOther,
            IntoleranceRollup: intoleranceRollup,
            IntoleranceOtherEntries: intoleranceOther,
            Days: days,
            People: people,
            EventTodayDate: eventTodayDate);
    }

    public int GetCurrentWeekStartOffsetForActiveEvent(EventSettings eventSettings, Instant now)
    {
        ArgumentNullException.ThrowIfNull(eventSettings);
        var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(eventSettings.TimeZoneId)
            ?? DateTimeZoneProviders.Tzdb["Europe/Madrid"];
        var todayLocal = now.InZone(zone).Date;
        // NodaTime: IsoDayOfWeek.Monday = 1; LocalDate.DayOfWeek is an IsoDayOfWeek.
        var daysSinceMonday = ((int)todayLocal.DayOfWeek - 1 + DaysPerWeek) % DaysPerWeek;
        var monday = todayLocal.PlusDays(-daysSinceMonday);
        return Period.Between(eventSettings.GateOpeningDate, monday, PeriodUnits.Days).Days;
    }

    public int GetCurrentDayOffsetForActiveEvent(EventSettings eventSettings, Instant now)
    {
        ArgumentNullException.ThrowIfNull(eventSettings);
        var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(eventSettings.TimeZoneId)
            ?? DateTimeZoneProviders.Tzdb["Europe/Madrid"];
        var todayLocal = now.InZone(zone).Date;
        return Period.Between(eventSettings.GateOpeningDate, todayLocal, PeriodUnits.Days).Days;
    }

    public async Task<DailyMatrixDto> GetDailyRosterAsync(int dayOffset, CancellationToken ct = default)
    {
        var eventSettings = await _shiftMgmt.GetActiveAsync().ConfigureAwait(false);
        var calendarDate = eventSettings is null
            ? (LocalDate?)null
            : eventSettings.GateOpeningDate.PlusDays(dayOffset);
        var eventName = eventSettings?.EventName;

        // "Today" must be in event timezone (same rationale as the weekly view —
        // a Madrid coordinator late in the evening must not see tomorrow flagged).
        LocalDate? eventTodayDate = null;
        if (eventSettings is not null)
        {
            var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(eventSettings.TimeZoneId)
                ?? DateTimeZoneProviders.Tzdb["Europe/Madrid"];
            eventTodayDate = _clock.GetCurrentInstant().InZone(zone).Date;
        }

        // Monday-of-week containing this day. With active event use NodaTime
        // day-of-week so the result respects ISO Monday=1. Fallback when no
        // active event: pure offset arithmetic so the "← back to weekly"
        // link still routes to a valid week. Both branches return the
        // gate-opening-relative offset of that Monday.
        int weekStartOffset;
        if (calendarDate is { } cd)
        {
            var daysSinceMonday = ((int)cd.DayOfWeek - 1 + DaysPerWeek) % DaysPerWeek;
            weekStartOffset = dayOffset - daysSinceMonday;
        }
        else
        {
            // Without an event we don't know which day-offset is a Monday;
            // bucket on the integer offset modulo 7 so prev/next-week links
            // still partition cleanly.
            weekStartOffset = dayOffset - ((dayOffset % DaysPerWeek + DaysPerWeek) % DaysPerWeek);
        }

        var userIds = await _shiftMgmt.GetOnSiteUserIdsForDayAsync(dayOffset, ct).ConfigureAwait(false);

        if (userIds.Count == 0)
        {
            return new DailyMatrixDto(
                DayOffset: dayOffset,
                CalendarDate: calendarDate,
                EventTodayDate: eventTodayDate,
                EventName: eventName,
                WeekStartOffset: weekStartOffset,
                TotalOnSite: 0,
                UnansweredCount: 0,
                DietaryBreakdown: EmptyDietaryBreakdown(),
                AllergyRollup: EmptyRollup(DietaryOptions.AllergyOptions),
                AllergyOtherEntries: Array.Empty<string>(),
                IntoleranceRollup: EmptyRollup(DietaryOptions.IntoleranceOptions),
                IntoleranceOtherEntries: Array.Empty<string>(),
                People: Array.Empty<DailyPersonRowDto>());
        }

        // Dietary from the cached UserInfo for the day's cohort.
        var profileByUserId = BuildProfileMap(await _userRead.GetUserInfosAsync(userIds, ct).ConfigureAwait(false));

        // People — built in repo-order (caller is expected to sort for display
        // via CantinaRosterAssembler.WithSortedPeople; see
        // memory/architecture/display-sort-in-controllers.md).
        var people = new List<DailyPersonRowDto>(userIds.Count);
        foreach (var id in userIds)
        {
            profileByUserId.TryGetValue(id, out var profile);

            IReadOnlySet<string> allergies = profile?.Allergies is { Count: > 0 } a
                ? new HashSet<string>(a, StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            IReadOnlySet<string> intolerances = profile?.Intolerances is { Count: > 0 } i
                ? new HashSet<string>(i, StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);

            people.Add(new DailyPersonRowDto(
                UserId: id,
                BurnerName: ResolveBurnerName(profile),
                DietaryPreference: profile?.DietaryPreference,
                Allergies: allergies,
                AllergyOtherText: profile?.AllergyOtherText,
                Intolerances: intolerances,
                IntoleranceOtherText: profile?.IntoleranceOtherText));
        }

        // Aggregates are over the day's cohort (no week dedup needed — each
        // user appears exactly once in userIds for this single day).
        var dayProfiles = new List<ProfileInfo>(userIds.Count);
        foreach (var id in userIds)
        {
            if (profileByUserId.TryGetValue(id, out var p))
                dayProfiles.Add(p);
        }

        var dietaryBreakdown = BuildDietaryBreakdown(dayProfiles, userIds.Count);
        var (allergyRollup, allergyOther) = BuildRollup(
            dayProfiles,
            p => p.Allergies,
            p => p.AllergyOtherText,
            DietaryOptions.AllergyOptions);
        var (intoleranceRollup, intoleranceOther) = BuildRollup(
            dayProfiles,
            p => p.Intolerances,
            p => p.IntoleranceOtherText,
            DietaryOptions.IntoleranceOptions);

        var answeredCount = dayProfiles.Count(p => !string.IsNullOrEmpty(p.DietaryPreference));
        var unanswered = userIds.Count - answeredCount;

        return new DailyMatrixDto(
            DayOffset: dayOffset,
            CalendarDate: calendarDate,
            EventTodayDate: eventTodayDate,
            EventName: eventName,
            WeekStartOffset: weekStartOffset,
            TotalOnSite: userIds.Count,
            UnansweredCount: unanswered,
            DietaryBreakdown: dietaryBreakdown,
            AllergyRollup: allergyRollup,
            AllergyOtherEntries: allergyOther,
            IntoleranceRollup: intoleranceRollup,
            IntoleranceOtherEntries: intoleranceOther,
            People: people);
    }

    private static Dictionary<Guid, ProfileInfo> BuildProfileMap(IReadOnlyDictionary<Guid, UserInfo> userInfos)
    {
        var map = new Dictionary<Guid, ProfileInfo>(userInfos.Count);
        foreach (var (id, info) in userInfos)
        {
            if (info.Profile is not null)
                map[id] = info.Profile;
        }
        return map;
    }

    private static string ResolveBurnerName(ProfileInfo? profile) =>
        profile is not null && !string.IsNullOrWhiteSpace(profile.BurnerName)
            ? profile.BurnerName
            : "(unknown)";

    private static IReadOnlyDictionary<string, int> EmptyDietaryBreakdown()
    {
        var dict = new Dictionary<string, int>(DietaryOptions.DietaryPreferences.Count + 1, StringComparer.Ordinal);
        foreach (var pref in DietaryOptions.DietaryPreferences)
            dict[pref] = 0;
        dict[UnansweredKey] = 0;
        return dict;
    }

    private static IReadOnlyList<RollupItemDto> EmptyRollup(IReadOnlyList<string> labels)
    {
        var rows = new List<RollupItemDto>(labels.Count);
        foreach (var label in labels)
            rows.Add(new RollupItemDto(label, 0));
        return rows;
    }

    private static IReadOnlyDictionary<string, int> BuildDietaryBreakdown(
        IReadOnlyList<ProfileInfo> uniqueProfiles, int totalUniqueOnSite)
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
            // Only bucket known preferences — unknown/legacy values would otherwise
            // distort the breakdown. Treat them as Unanswered for display purposes.
            if (dict.ContainsKey(profile.DietaryPreference))
                dict[profile.DietaryPreference]++;
        }

        dict[UnansweredKey] = totalUniqueOnSite - answered;
        return dict;
    }

    private static (IReadOnlyList<RollupItemDto> Rollup, IReadOnlyList<string> OtherEntries) BuildRollup(
        IReadOnlyList<ProfileInfo> uniqueProfiles,
        Func<ProfileInfo, IReadOnlyList<string>> pickChips,
        Func<ProfileInfo, string?> pickOtherText,
        IReadOnlyList<string> canonicalLabels)
    {
        var counts = new Dictionary<string, int>(canonicalLabels.Count, StringComparer.Ordinal);
        foreach (var label in canonicalLabels)
            counts[label] = 0;

        // Dedup other-text entries by trimmed value across the week.
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

        // Preserve canonical ordering of the rollup rows.
        var rollup = new List<RollupItemDto>(canonicalLabels.Count);
        foreach (var label in canonicalLabels)
            rollup.Add(new RollupItemDto(label, counts[label]));

        return (rollup, otherEntries);
    }
}
