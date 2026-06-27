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
        var eventTodayDate = GetEventTodayDate(eventSettings);

        // Per-day cohort: 7 sequential queries for the on-site user ids. At ~500
        // users this is fine (see CLAUDE.md scale notes).
        var perDay = await LoadWeeklyOnSiteUsersAsync(eventSettings, weekStartOffset, ct).ConfigureAwait(false);

        // Build the union of unique on-site user IDs across the week, and
        // map each user id to the set of calendar dates they were on-site.
        var daysOnSiteByUserId = BuildDaysOnSiteByUserId(perDay, weekStartOffset, weekStartDate);

        // Arrival-day feeding: each human is fed (present, but with no shift)
        // the day before their FIRST confirmed shift across the whole event,
        // not just this week. Scan the full build→strike range once, then inject
        // the day-before-first-shift into this week's on-site set when it lands
        // inside the visible 7 days. This pulls in humans whose only relation to
        // the week is their arrival day (no confirmed shift inside the week).
        // Guarded on an active event — the no-event branch leaves the map empty.
        if (eventSettings is not null && weekStartDate is { } anchor)
        {
            // Only arrivals landing in [weekStartOffset, weekStartOffset+6] are
            // injected below, so a first confirmed shift past weekStartOffset+7
            // can't produce an in-week arrival — cap the scan there.
            var firstConfirmedOffsetByUser =
                await BuildFirstConfirmedOffsetByUserAsync(
                    eventSettings, weekStartOffset + DaysPerWeek, ct).ConfigureAwait(false);
            foreach (var (userId, minOffset) in firstConfirmedOffsetByUser)
            {
                var arrivalOffset = minOffset - 1;
                if (arrivalOffset < weekStartOffset || arrivalOffset > weekStartOffset + DaysPerWeek - 1)
                    continue;

                var arrivalDate = anchor.PlusDays(arrivalOffset - weekStartOffset);
                if (!daysOnSiteByUserId.TryGetValue(userId, out var list))
                {
                    list = new List<LocalDate>(capacity: DaysPerWeek);
                    daysOnSiteByUserId[userId] = list;
                }

                list.Add(arrivalDate);
            }
        }

        var uniqueUserIds = daysOnSiteByUserId.Keys.ToList();

        // Dietary lives on Profile — read it from the cached UserInfo for the whole
        // on-site cohort in one batched, cache-friendly call. profileByUserId is the
        // single source for dietary preference / allergies / intolerances below.
        var profileByUserId = uniqueUserIds.Count == 0
            ? new Dictionary<Guid, ProfileInfo>()
            : BuildProfileMap(await _userRead.GetUserInfosAsync(uniqueUserIds, ct).ConfigureAwait(false));

        // Build per-day summaries (counts only) from the POST-injection on-site
        // map so arrival-day people are counted on their arrival date — keeping
        // the weekly strip consistent with the daily drill-down. Dietary is
        // per-user, so a day's "unanswered" count is over that day's user ids.
        var days = BuildDaySummaries(daysOnSiteByUserId, weekStartOffset, weekStartDate, profileByUserId);

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
        var uniqueProfiles = BuildUniqueProfiles(uniqueUserIds, profileByUserId);

        // The 7 calendar dates of the week, used to compute NoShift as the
        // complement of each person's on-site days. Empty when the week
        // has no anchor date (no active event) — in that branch we don't
        // reach this code path anyway since uniqueUserIds.Count == 0.
        var weekDays = BuildWeekDays(weekStartDate);

        // People are returned in unspecified order. Display sort happens at
        // the Web layer in CantinaRosterAssembler (see
        // memory/architecture/display-sort-in-controllers.md).
        var people = BuildWeeklyPeople(uniqueUserIds, profileByUserId, daysOnSiteByUserId, weekDays);

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
        var eventTodayDate = GetEventTodayDate(eventSettings);

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

        var shiftUserIds = eventSettings is null
            ? Array.Empty<Guid>()
            : await _shiftMgmt.GetOnSiteUserIdsForDayAsync(eventSettings.Id, dayOffset, ct).ConfigureAwait(false);

        // Arrival-day feeding: a human is fed (present, no shift) the day before
        // their FIRST confirmed shift across the whole event. For day N that
        // means anyone whose first confirmed shift offset == N+1 arrives today.
        // Union them with the day-N shift cohort so the drill-down stays
        // consistent with the weekly strip (which injects the same arrival day).
        // CRITICAL: compute the union BEFORE the empty-check — a day can have
        // zero shifts but still have arrivals, so the early-return must be
        // evaluated on the unioned set. Guarded on an active event; the no-event
        // branch leaves the set as the (empty) shift cohort.
        var unionedUserIds = new HashSet<Guid>(shiftUserIds);
        if (eventSettings is not null)
        {
            // Only users whose first confirmed shift is exactly dayOffset+1 are
            // pulled in, so the scan never needs to look past dayOffset+1.
            var firstConfirmedOffsetByUser =
                await BuildFirstConfirmedOffsetByUserAsync(
                    eventSettings, dayOffset + 1, ct).ConfigureAwait(false);
            foreach (var (userId, minOffset) in firstConfirmedOffsetByUser)
            {
                if (minOffset == dayOffset + 1)
                    unionedUserIds.Add(userId);
            }
        }

        var userIds = unionedUserIds.ToList();

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

    private async Task<List<(int DayOffset, IReadOnlyList<Guid> UserIds)>> LoadWeeklyOnSiteUsersAsync(
        EventSettings? eventSettings,
        int weekStartOffset,
        CancellationToken ct)
    {
        var perDay = new List<(int DayOffset, IReadOnlyList<Guid> UserIds)>(DaysPerWeek);
        for (var i = 0; i < DaysPerWeek; i++)
        {
            var dayOffset = weekStartOffset + i;
            var userIds = eventSettings is null
                ? Array.Empty<Guid>()
                : await _shiftMgmt.GetOnSiteUserIdsForDayAsync(eventSettings.Id, dayOffset, ct)
                    .ConfigureAwait(false);
            perDay.Add((dayOffset, userIds));
        }

        return perDay;
    }

    /// <summary>
    /// Scans from build start up through <paramref name="scanThroughOffset"/>
    /// (clamped to strike end, inclusive) and records, per human, the earliest
    /// day offset on which they have a confirmed on-site signup. Used to feed
    /// each human the day before their first confirmed shift. Kept separate from
    /// <see cref="LoadWeeklyOnSiteUsersAsync"/> — that load stays scoped to the
    /// visible week; this one scans from the start of the event so a true first
    /// day is never missed. The upper bound is the caller's window end: a first
    /// confirmed shift later than that yields an arrival day outside the window,
    /// which the caller discards anyway, so scanning past it is wasted DB work.
    /// </summary>
    private async Task<Dictionary<Guid, int>> BuildFirstConfirmedOffsetByUserAsync(
        EventSettings ev,
        int scanThroughOffset,
        CancellationToken ct)
    {
        var firstOffsetByUser = new Dictionary<Guid, int>();
        var lastOffset = Math.Min(ev.StrikeEndOffset, scanThroughOffset);
        for (var offset = ev.BuildStartOffset; offset <= lastOffset; offset++)
        {
            var userIds = await _shiftMgmt.GetOnSiteUserIdsForDayAsync(ev.Id, offset, ct).ConfigureAwait(false);
            foreach (var id in userIds)
            {
                if (!firstOffsetByUser.TryGetValue(id, out var existing) || offset < existing)
                    firstOffsetByUser[id] = offset;
            }
        }

        return firstOffsetByUser;
    }

    private static Dictionary<Guid, List<LocalDate>> BuildDaysOnSiteByUserId(
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

    private static List<DayRosterSummaryDto> BuildDaySummaries(
        IReadOnlyDictionary<Guid, List<LocalDate>> daysOnSiteByUserId,
        int weekStartOffset,
        LocalDate? weekStartDate,
        IReadOnlyDictionary<Guid, ProfileInfo> profileByUserId)
    {
        var days = new List<DayRosterSummaryDto>(DaysPerWeek);
        for (var i = 0; i < DaysPerWeek; i++)
        {
            var calendarDate = weekStartDate?.PlusDays(i);

            // Count users on-site this calendar date from the post-injection map
            // (includes arrival-day people). Without an anchor date the map holds
            // no dates, so every day is correctly zero.
            var total = 0;
            var unanswered = 0;
            if (calendarDate is { } day)
            {
                foreach (var (id, dates) in daysOnSiteByUserId)
                {
                    if (!dates.Contains(day))
                        continue;

                    total++;
                    if (!profileByUserId.TryGetValue(id, out var profile)
                        || string.IsNullOrEmpty(profile.DietaryPreference))
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

    private static List<ProfileInfo> BuildUniqueProfiles(
        IReadOnlyList<Guid> uniqueUserIds,
        IReadOnlyDictionary<Guid, ProfileInfo> profileByUserId)
    {
        var uniqueProfiles = new List<ProfileInfo>(uniqueUserIds.Count);
        foreach (var id in uniqueUserIds)
        {
            if (profileByUserId.TryGetValue(id, out var profile))
                uniqueProfiles.Add(profile);
        }

        return uniqueProfiles;
    }

    private static LocalDate[] BuildWeekDays(LocalDate? weekStartDate)
        => weekStartDate is null
            ? Array.Empty<LocalDate>()
            : Enumerable.Range(0, DaysPerWeek)
                .Select(i => weekStartDate.Value.PlusDays(i))
                .ToArray();

    private static List<RosterPersonDto> BuildWeeklyPeople(
        IReadOnlyList<Guid> uniqueUserIds,
        IReadOnlyDictionary<Guid, ProfileInfo> profileByUserId,
        IReadOnlyDictionary<Guid, List<LocalDate>> daysOnSiteByUserId,
        IReadOnlyList<LocalDate> weekDays)
    {
        var people = new List<RosterPersonDto>(uniqueUserIds.Count);
        foreach (var id in uniqueUserIds)
        {
            profileByUserId.TryGetValue(id, out var profile);
            var daysList = daysOnSiteByUserId[id];
            daysList.Sort();

            people.Add(new RosterPersonDto(
                UserId: id,
                BurnerName: ResolveBurnerName(profile),
                ArrivesOn: daysList[0],
                NoShift: BuildNoShiftDays(daysList, weekDays),
                DietaryPreference: profile?.DietaryPreference,
                Allergies: profile?.Allergies is { Count: > 0 } a ? a.ToArray() : Array.Empty<string>(),
                AllergyOtherText: profile?.AllergyOtherText,
                Intolerances: profile?.Intolerances is { Count: > 0 } i ? i.ToArray() : Array.Empty<string>(),
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

    private LocalDate? GetEventTodayDate(EventSettings? eventSettings)
    {
        if (eventSettings is null)
            return null;

        var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(eventSettings.TimeZoneId)
            ?? DateTimeZoneProviders.Tzdb["Europe/Madrid"];
        return _clock.GetCurrentInstant().InZone(zone).Date;
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
