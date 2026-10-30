using Humans.Cantina.Domain;
using Humans.AuditLog.Contracts;
using Humans.Base.Extensions;
using Humans.Settings.Contracts;
using Humans.Shifts.Contracts;
using Humans.Cantina.Contracts;
using Humans.Cantina.Services.Dtos;
using NodaTime;
using Humans.Users.Contracts;
using static Humans.Cantina.Services.CantinaRosterCalculation;

namespace Humans.Cantina.Services;

/// <summary>
/// <c>MedicalConditions</c> is never read here: the cantina plans around food, not medical history.
/// </summary>
internal sealed class CantinaRosterService : ICantinaRosterService
{
    private readonly IShiftService _shiftMgmt;
    private readonly ISettingsService _settingsService;
    private readonly IUserService _userRead;
    private readonly ICantinaService _dietary;
    private readonly IClock _clock;
    private readonly IAuditLogService _audit;

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

    public async Task<WeeklyRosterDto> GetWeeklyRosterAsync(int? weekStartOffset = null, RosterDisclosure? disclosure = null, CancellationToken ct = default)
    {
        var burn = await _settingsService.GetActiveEventSettingsAsync(ct).ConfigureAwait(false);
        var roster = await BuildWeeklyRosterAsync(
            burn, weekStartOffset ?? CurrentWeekStartOffset(burn), ct).ConfigureAwait(false);
        if (disclosure is not null)
        {
            // One row per disclosure, never one per member shown: the roster is the kitchen assembling a few hundred
            // people's allergies, not someone looking up one person's health data.
            var scope = $"week of {roster.WeekStartDate.ToInvariantDate() ?? "no active event"}";
            await _audit.LogAsync(disclosure.Action, "EventSettings", burn?.Id ?? Guid.Empty,
                $"Weekly Cantina roster {disclosure.Verb}: {roster.People.Count} people rows, {scope}",
                disclosure.ActorUserId).ConfigureAwait(false);
        }
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

        // Arrival day: the day before a human's FIRST confirmed shift of the whole event, not just this week, so scan
        // from build start. It is every cohort member's ArrivesOn, and is fed as an on-site day when it lands in the week.
        var arrivesOnByUser = new Dictionary<Guid, LocalDate>();
        if (burn is not null && weekStartDate is { } anchor)
        {
            // Every cohort member has a confirmed shift by weekStartOffset+7 (an arrival-only human's first shift is
            // that day), so a later first shift belongs to nobody on this roster.
            var firstConfirmedOffsetByUser =
                await BuildFirstConfirmedOffsetByUserAsync(
                    burn, weekStartOffset + DaysPerWeek, onSiteByOffset, ct).ConfigureAwait(false);
            foreach (var (userId, minOffset) in firstConfirmedOffsetByUser)
            {
                var arrivalOffset = minOffset - 1;
                var arrivalDate = anchor.PlusDays(arrivalOffset - weekStartOffset);
                arrivesOnByUser[userId] = arrivalDate;
                if (arrivalOffset < weekStartOffset || arrivalOffset > weekStartOffset + DaysPerWeek - 1)
                    continue;

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

        var people = BuildWeeklyPeople(uniqueUserIds, userInfoById, dietById, daysOnSiteByUserId, arrivesOnByUser, weekDays);

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

    public async Task<DailyMatrixDto> GetDailyRosterAsync(int? dayOffset = null, RosterDisclosure? disclosure = null, CancellationToken ct = default)
    {
        var burn = await _settingsService.GetActiveEventSettingsAsync(ct).ConfigureAwait(false);
        var matrix = await BuildDailyRosterAsync(
            burn, dayOffset ?? CurrentDayOffset(burn), ct).ConfigureAwait(false);
        if (disclosure is not null)
        {
            var scope = $"day {matrix.CalendarDate.ToInvariantDate() ?? "no active event"}";
            await _audit.LogAsync(disclosure.Action, "EventSettings", burn?.Id ?? Guid.Empty,
                $"Daily Cantina roster {disclosure.Verb}: {matrix.People.Count} people rows, {scope}",
                disclosure.ActorUserId).ConfigureAwait(false);
        }
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

    private LocalDate? GetEventTodayDate(EventSettingsInfo? burn)
    {
        if (burn is null)
            return null;

        var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(burn.TimeZoneId)
            ?? DateTimeZoneProviders.Tzdb["Europe/Madrid"];
        return _clock.GetCurrentInstant().InZone(zone).Date;
    }
}
