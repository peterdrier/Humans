using Humans.Cantina.Domain;
using Humans.AuditLog.Contracts;
using Humans.Base.Extensions;
using Humans.Settings.Contracts;
using Humans.Shifts.Contracts;
using Humans.Cantina.Contracts;
using Humans.Cantina.Services.Dtos;
using NodaTime;
using Humans.Users.Contracts;

namespace Humans.Cantina.Services;

/// <summary>
/// <c>MedicalConditions</c> is never read here: the cantina plans around food, not medical history.
/// </summary>
internal sealed class CantinaRosterService : ICantinaRosterService
{
    private const int DaysPerWeek = 7;

    private readonly IShiftService _shiftMgmt;
    private readonly ISettingsService _settingsService;
    private readonly IUserService _userRead;
    private readonly ICantinaService _dietary;
    private readonly IClock _clock;
    private readonly IAuditLogService _audit;

    // Must match the last entry of Roster.cshtml's dietaryOrder; the service tests pin the literal.
    private static readonly string UnansweredKey = "Unanswered";

    public CantinaRosterService(
        IShiftService shiftMgmt,
        ISettingsService settingsService,
        IUserService userRead,
        ICantinaService dietary,
        IClock clock,
        IAuditLogService audit)
    {
        _shiftMgmt = shiftMgmt;
        _settingsService = settingsService;
        _userRead = userRead;
        _dietary = dietary;
        _clock = clock;
        _audit = audit;
    }

    public async Task<WeeklyRosterDto> GetWeeklyRosterAsync(int? weekStartOffset = null, Guid? exportActorUserId = null, CancellationToken ct = default)
    {
        var burn = await _settingsService.GetActiveEventSettingsAsync(ct).ConfigureAwait(false);
        var roster = await BuildWeeklyRosterAsync(
            burn, weekStartOffset ?? CurrentWeekStartOffset(burn), ct).ConfigureAwait(false);
        if (exportActorUserId is { } actor)
            await _audit.LogAsync(AuditAction.CantinaRosterExported, "EventSettings", burn?.Id ?? Guid.Empty,
                $"Weekly Cantina roster exported: {roster.People.Count} people rows, week of {roster.WeekStartDate.ToInvariantDate() ?? "no active event"}",
                actor).ConfigureAwait(false);
        return roster;
    }

    private async Task<WeeklyRosterDto> BuildWeeklyRosterAsync(
        EventSettingsInfo? burn, int weekStartOffset, CancellationToken ct)
    {
        var weekStartDate = burn is null
            ? (LocalDate?)null
            : burn.GateOpeningDate.PlusDays(weekStartOffset);
        var weekEndDate = weekStartDate?.PlusDays(DaysPerWeek - 1);
        var eventName = burn?.EventName;

        // In the event timezone: a UTC "today" highlighted tomorrow for a Madrid coordinator late in the evening.
        var eventTodayDate = GetEventTodayDate(burn);

        // The visible week and the arrival scan overlap; fetch each offset once per request.
        var onSiteByOffset = new Dictionary<int, IReadOnlyList<Guid>>();

        var perDay = await LoadWeeklyOnSiteUsersAsync(burn, weekStartOffset, onSiteByOffset, ct).ConfigureAwait(false);

        var daysOnSiteByUserId = BuildDaysOnSiteByUserId(perDay, weekStartOffset, weekStartDate);

        // Arrival-day feeding: the day before a human's FIRST confirmed shift of the whole event, not just this week,
        // so scan from build start and inject arrivals that land in the visible week.
        if (burn is not null && weekStartDate is { } anchor)
        {
            // Only in-week arrivals are injected, so a first shift past weekStartOffset+7 can't matter.
            var firstConfirmedOffsetByUser =
                await BuildFirstConfirmedOffsetByUserAsync(
                    burn, weekStartOffset + DaysPerWeek, onSiteByOffset, ct).ConfigureAwait(false);
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

        var userInfoById = uniqueUserIds.Count == 0
            ? new Dictionary<Guid, UserInfo>()
            : await _userRead.GetUserInfosAsync(uniqueUserIds, ct).ConfigureAwait(false);
        var dietById = await _dietary.GetDietaryProfilesAsync(uniqueUserIds, ct).ConfigureAwait(false);

        // From the post-injection map, so arrival-day people count on their arrival date, consistent with the daily
        // drill-down.
        var days = BuildDaySummaries(daysOnSiteByUserId, weekStartOffset, weekStartDate, dietById);

        // Every aggregate below is over unique humans: a person on site several days counts once.
        var uniqueProfiles = BuildUniqueProfiles(uniqueUserIds, dietById);

        var weekDays = BuildWeekDays(weekStartDate);

        var people = BuildWeeklyPeople(uniqueUserIds, userInfoById, dietById, daysOnSiteByUserId, weekDays);

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

        var answeredCount = uniqueProfiles.Count(p => p.HasPreference);
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

    /// <summary>Zero without an active event: nothing to be relative to.</summary>
    private int CurrentWeekStartOffset(EventSettingsInfo? burn)
    {
        if (burn is null || GetEventTodayDate(burn) is not { } today)
            return 0;

        return Period.Between(burn.GateOpeningDate, today.PlusDays(-DaysSinceMonday(today)), PeriodUnits.Days).Days;
    }

    private static int DaysSinceMonday(LocalDate date) => (int)date.DayOfWeek - (int)IsoDayOfWeek.Monday;

    /// <summary>Zero without an active event.</summary>
    private int CurrentDayOffset(EventSettingsInfo? burn)
        => burn is not null && GetEventTodayDate(burn) is { } today
            ? Period.Between(burn.GateOpeningDate, today, PeriodUnits.Days).Days
            : 0;

    public async Task<DailyMatrixDto> GetDailyRosterAsync(int? dayOffset = null, Guid? exportActorUserId = null, CancellationToken ct = default)
    {
        var burn = await _settingsService.GetActiveEventSettingsAsync(ct).ConfigureAwait(false);
        var matrix = await BuildDailyRosterAsync(
            burn, dayOffset ?? CurrentDayOffset(burn), ct).ConfigureAwait(false);
        if (exportActorUserId is { } actor)
            await _audit.LogAsync(AuditAction.CantinaRosterExported, "EventSettings", burn?.Id ?? Guid.Empty,
                $"Daily Cantina roster exported: {matrix.People.Count} people rows, day {matrix.CalendarDate.ToInvariantDate() ?? "no active event"}",
                actor).ConfigureAwait(false);
        return matrix;
    }

    private async Task<DailyMatrixDto> BuildDailyRosterAsync(
        EventSettingsInfo? burn, int dayOffset, CancellationToken ct)
    {
        var calendarDate = burn is null
            ? (LocalDate?)null
            : burn.GateOpeningDate.PlusDays(dayOffset);
        var eventName = burn?.EventName;

        // Event timezone, same reason as the weekly view.
        var eventTodayDate = GetEventTodayDate(burn);

        int weekStartOffset;
        if (calendarDate is { } cd)
        {
            weekStartOffset = dayOffset - DaysSinceMonday(cd);
        }
        else
        {
            // Without an event no offset is known to be a Monday; bucket modulo 7 so prev/next links still partition.
            weekStartOffset = dayOffset - ((dayOffset % DaysPerWeek + DaysPerWeek) % DaysPerWeek);
        }

        // The arrival scan re-reads this day, so both share one per-request cache.
        var onSiteByOffset = new Dictionary<int, IReadOnlyList<Guid>>();

        var shiftUserIds = burn is null
            ? Array.Empty<Guid>()
            : await GetOnSiteUserIdsAsync(burn.Id, dayOffset, onSiteByOffset, ct).ConfigureAwait(false);

        // Arrival-day feeding: anyone whose first confirmed shift is day N+1 arrives today. A day with zero shifts can
        // still have arrivals.
        var unionedUserIds = new HashSet<Guid>(shiftUserIds);
        if (burn is not null)
        {
            var firstConfirmedOffsetByUser =
                await BuildFirstConfirmedOffsetByUserAsync(
                    burn, dayOffset + 1, onSiteByOffset, ct).ConfigureAwait(false);
            foreach (var (userId, minOffset) in firstConfirmedOffsetByUser)
            {
                if (minOffset == dayOffset + 1)
                    unionedUserIds.Add(userId);
            }
        }

        var userIds = unionedUserIds.ToList();

        var userInfoById = userIds.Count == 0
            ? new Dictionary<Guid, UserInfo>()
            : await _userRead.GetUserInfosAsync(userIds, ct).ConfigureAwait(false);
        var dietById = await _dietary.GetDietaryProfilesAsync(userIds, ct).ConfigureAwait(false);

        var people = new List<DailyPersonRowDto>(userIds.Count);
        foreach (var id in userIds)
        {
            userInfoById.TryGetValue(id, out var info);
            dietById.TryGetValue(id, out var profile);

            people.Add(new DailyPersonRowDto(
                UserId: id,
                BurnerName: ResolveBurnerName(info),
                DietaryPreference: profile?.DietaryPreference,
                Allergies: new HashSet<string>(profile?.Allergies ?? [], StringComparer.Ordinal),
                AllergyOtherText: profile?.AllergyOtherText,
                Intolerances: new HashSet<string>(profile?.Intolerances ?? [], StringComparer.Ordinal),
                IntoleranceOtherText: profile?.IntoleranceOtherText));
        }

        // The view and CSV writer count the chip totals from People; only the unanswered headline is owed here.
        var answeredCount = people.Count(p => !string.IsNullOrEmpty(p.DietaryPreference));
        var unanswered = userIds.Count - answeredCount;

        return new DailyMatrixDto(
            DayOffset: dayOffset,
            CalendarDate: calendarDate,
            EventTodayDate: eventTodayDate,
            EventName: eventName,
            WeekStartOffset: weekStartOffset,
            TotalOnSite: userIds.Count,
            UnansweredCount: unanswered,
            People: people);
    }

    private async Task<List<(int DayOffset, IReadOnlyList<Guid> UserIds)>> LoadWeeklyOnSiteUsersAsync(
        EventSettingsInfo? burn,
        int weekStartOffset,
        Dictionary<int, IReadOnlyList<Guid>> onSiteByOffset,
        CancellationToken ct)
    {
        var perDay = new List<(int DayOffset, IReadOnlyList<Guid> UserIds)>(DaysPerWeek);
        for (var i = 0; i < DaysPerWeek; i++)
        {
            var dayOffset = weekStartOffset + i;
            var userIds = burn is null
                ? Array.Empty<Guid>()
                : await GetOnSiteUserIdsAsync(burn.Id, dayOffset, onSiteByOffset, ct).ConfigureAwait(false);
            perDay.Add((dayOffset, userIds));
        }

        return perDay;
    }

    /// <summary>Memoized per request: the visible week and the arrival scan overlap.</summary>
    private async Task<IReadOnlyList<Guid>> GetOnSiteUserIdsAsync(
        Guid burnId,
        int dayOffset,
        Dictionary<int, IReadOnlyList<Guid>> onSiteByOffset,
        CancellationToken ct)
    {
        if (onSiteByOffset.TryGetValue(dayOffset, out var cached))
            return cached;

        var userIds = await _shiftMgmt.GetOnSiteUserIdsForDayAsync(burnId, dayOffset, ct).ConfigureAwait(false);
        onSiteByOffset[dayOffset] = userIds;
        return userIds;
    }

    /// <summary>
    /// Earliest confirmed on-site day per human, from build start: a week-scoped load would miss a true first day.
    /// Stops at <paramref name="scanThroughOffset"/>, since a later first shift yields an arrival the caller discards.
    /// </summary>
    private async Task<Dictionary<Guid, int>> BuildFirstConfirmedOffsetByUserAsync(
        EventSettingsInfo burn,
        int scanThroughOffset,
        Dictionary<int, IReadOnlyList<Guid>> onSiteByOffset,
        CancellationToken ct)
    {
        var firstOffsetByUser = new Dictionary<Guid, int>();
        var lastOffset = Math.Min(burn.StrikeEndOffset, scanThroughOffset);
        for (var offset = burn.BuildStartOffset; offset <= lastOffset; offset++)
        {
            var userIds = await GetOnSiteUserIdsAsync(burn.Id, offset, onSiteByOffset, ct).ConfigureAwait(false);
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

    private static List<DietaryProfileInfo> BuildUniqueProfiles(
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

    private static LocalDate[] BuildWeekDays(LocalDate? weekStartDate)
        => weekStartDate is null
            ? Array.Empty<LocalDate>()
            : Enumerable.Range(0, DaysPerWeek)
                .Select(i => weekStartDate.Value.PlusDays(i))
                .ToArray();

    private static List<RosterPersonDto> BuildWeeklyPeople(
        IReadOnlyList<Guid> uniqueUserIds,
        IReadOnlyDictionary<Guid, UserInfo> userInfoById,
        IReadOnlyDictionary<Guid, DietaryProfileInfo> dietById,
        IReadOnlyDictionary<Guid, List<LocalDate>> daysOnSiteByUserId,
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
                ArrivesOn: daysList[0],
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

    private LocalDate? GetEventTodayDate(EventSettingsInfo? burn)
    {
        if (burn is null)
            return null;

        var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(burn.TimeZoneId)
            ?? DateTimeZoneProviders.Tzdb["Europe/Madrid"];
        return _clock.GetCurrentInstant().InZone(zone).Date;
    }

    /// <summary>
    /// The name Users already resolved; <c>ProfileInfo.BurnerName</c> would show "(unknown)" for a human whose name lives
    /// only on the user row. The fallback covers a user id the cohort read did not return.
    /// </summary>
    private static string ResolveBurnerName(UserInfo? info) =>
        info is not null && !string.IsNullOrWhiteSpace(info.BurnerName)
            ? info.BurnerName
            : "(unknown)";

    private static IReadOnlyDictionary<string, int> BuildDietaryBreakdown(
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

    private static (IReadOnlyList<RollupItemDto> Rollup, IReadOnlyList<string> OtherEntries) BuildRollup(
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
