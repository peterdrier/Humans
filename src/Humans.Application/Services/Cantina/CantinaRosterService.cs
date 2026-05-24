using Humans.Application.Interfaces.Cantina;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Cantina.Dtos;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using NodaTime;
using ProfileEntity = Humans.Domain.Entities.Profile;

namespace Humans.Application.Services.Cantina;

/// <summary>
/// Application-layer implementation of <see cref="ICantinaRosterService"/>.
/// Pulls the on-site cohort and their <c>VolunteerEventProfile</c> rows for
/// each of the 7 days Mon–Sun from <c>IShiftManagementRepository</c>,
/// unions them into a unique-humans cohort, stitches burner-name labels
/// from <c>IProfileService</c> / <c>IUserService</c>, and computes the
/// weekly aggregates the Cantina coordinator UI needs. Medical fields never
/// leave the service — they are not present on any DTO.
/// </summary>
public sealed class CantinaRosterService : ICantinaRosterService
{
    private const int DaysPerWeek = 7;

    private readonly IShiftManagementRepository _shiftRepo;
    private readonly IProfileService _profileService;
    private readonly IUserService _userService;
    private readonly IClock _clock;

    // Canonical preference labels, with the "Unanswered" pseudo-bucket.
    private static readonly string UnansweredKey = "Unanswered";

    public CantinaRosterService(
        IShiftManagementRepository shiftRepo,
        IProfileService profileService,
        IUserService userService,
        IClock clock)
    {
        _shiftRepo = shiftRepo;
        _profileService = profileService;
        _userService = userService;
        _clock = clock;
    }

    public async Task<WeeklyRosterDto> GetWeeklyRosterAsync(int weekStartOffset, CancellationToken ct = default)
    {
        var eventSettings = await _shiftRepo.GetActiveEventSettingsAsync(ct).ConfigureAwait(false);
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

        // Per-day fetch: 7 sequential queries. At ~500 users this is fine
        // (see CLAUDE.md scale notes). Each tuple holds (dayOffset, userIds, veps).
        var perDay = new List<(int DayOffset, IReadOnlyList<Guid> UserIds, IReadOnlyList<VolunteerEventProfile> Veps)>(DaysPerWeek);
        for (var i = 0; i < DaysPerWeek; i++)
        {
            var dayOffset = weekStartOffset + i;
            var userIds = await _shiftRepo.GetOnSiteUserIdsForDayAsync(dayOffset, ct).ConfigureAwait(false);
            var veps = userIds.Count == 0
                ? Array.Empty<VolunteerEventProfile>()
                : await _shiftRepo.GetOnSiteVolunteerProfilesForDayAsync(dayOffset, ct).ConfigureAwait(false);
            perDay.Add((dayOffset, userIds, veps));
        }

        // Build the union of unique on-site user IDs across the week, and
        // map each user id to the set of calendar dates they were on-site.
        var daysOnSiteByUserId = new Dictionary<Guid, List<LocalDate>>();
        foreach (var (dayOffset, userIds, _) in perDay)
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

        // Unique-humans cohort: keyed by user id, with the latest VEP we saw
        // for them across the week (VEPs are per-user not per-shift; for any
        // given user they're identical across days the user appeared).
        var uniqueUserIds = daysOnSiteByUserId.Keys.ToList();
        var vepByUserId = new Dictionary<Guid, VolunteerEventProfile>();
        foreach (var (_, _, veps) in perDay)
        {
            foreach (var vep in veps)
                vepByUserId[vep.UserId] = vep;
        }

        // Build per-day summaries from the per-day data (counts only).
        var days = new List<DayRosterSummaryDto>(DaysPerWeek);
        for (var i = 0; i < DaysPerWeek; i++)
        {
            var (dayOffset, userIds, veps) = perDay[i];
            var vepsById = new Dictionary<Guid, VolunteerEventProfile>(veps.Count);
            foreach (var v in veps)
                vepsById[v.UserId] = v;
            var unanswered = 0;
            foreach (var id in userIds)
            {
                if (!vepsById.TryGetValue(id, out var v) || string.IsNullOrEmpty(v.DietaryPreference))
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

        var profiles = await _profileService.GetByUserIdsAsync(uniqueUserIds, ct).ConfigureAwait(false);
        var users = await _userService.GetByIdsAsync(uniqueUserIds, ct).ConfigureAwait(false);

        // Build the unique-humans VEP cohort once — every aggregate below
        // is computed over this list so a person on-site multiple days
        // contributes exactly once.
        var uniqueVeps = new List<VolunteerEventProfile>(uniqueUserIds.Count);
        foreach (var id in uniqueUserIds)
        {
            if (vepByUserId.TryGetValue(id, out var v))
                uniqueVeps.Add(v);
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
            profiles.TryGetValue(id, out var profile);
            users.TryGetValue(id, out var user);
            vepByUserId.TryGetValue(id, out var vep);
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
                BurnerName: ResolveBurnerName(profile, user),
                ArrivesOn: arrivesOn,
                NoShift: noShift,
                DietaryPreference: vep?.DietaryPreference,
                Allergies: vep?.Allergies is { Count: > 0 } a ? a.ToArray() : Array.Empty<string>(),
                AllergyOtherText: vep?.AllergyOtherText,
                Intolerances: vep?.Intolerances is { Count: > 0 } i ? i.ToArray() : Array.Empty<string>(),
                IntoleranceOtherText: vep?.IntoleranceOtherText));
        }

        var dietaryBreakdown = BuildDietaryBreakdown(uniqueVeps, uniqueUserIds.Count);
        var (allergyRollup, allergyOther) = BuildRollup(
            uniqueVeps,
            vep => vep.Allergies,
            vep => vep.AllergyOtherText,
            DietaryOptions.AllergyOptions);
        var (intoleranceRollup, intoleranceOther) = BuildRollup(
            uniqueVeps,
            vep => vep.Intolerances,
            vep => vep.IntoleranceOtherText,
            DietaryOptions.IntoleranceOptions);

        // "Unanswered" = unique on-site humans whose DietaryPreference is
        // null/empty, which includes both "no VEP at all" and "VEP exists
        // but DietaryPreference is blank".
        var answeredCount = uniqueVeps.Count(v => !string.IsNullOrEmpty(v.DietaryPreference));
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

    private static string ResolveBurnerName(ProfileEntity? profile, User? user)
    {
        if (profile is not null && !string.IsNullOrWhiteSpace(profile.BurnerName))
            return profile.BurnerName;
        if (user is not null && !string.IsNullOrWhiteSpace(user.DisplayName))
            return user.DisplayName;
        return "(unknown)";
    }

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
        IReadOnlyList<VolunteerEventProfile> uniqueVeps, int totalUniqueOnSite)
    {
        var dict = new Dictionary<string, int>(DietaryOptions.DietaryPreferences.Count + 1, StringComparer.Ordinal);
        foreach (var pref in DietaryOptions.DietaryPreferences)
            dict[pref] = 0;

        var answered = 0;
        foreach (var vep in uniqueVeps)
        {
            if (string.IsNullOrEmpty(vep.DietaryPreference))
                continue;

            answered++;
            // Only bucket known preferences — unknown/legacy values would otherwise
            // distort the breakdown. Treat them as Unanswered for display purposes.
            if (dict.ContainsKey(vep.DietaryPreference))
                dict[vep.DietaryPreference]++;
        }

        dict[UnansweredKey] = totalUniqueOnSite - answered;
        return dict;
    }

    private static (IReadOnlyList<RollupItemDto> Rollup, IReadOnlyList<string> OtherEntries) BuildRollup(
        IReadOnlyList<VolunteerEventProfile> uniqueVeps,
        Func<VolunteerEventProfile, List<string>> pickChips,
        Func<VolunteerEventProfile, string?> pickOtherText,
        IReadOnlyList<string> canonicalLabels)
    {
        var counts = new Dictionary<string, int>(canonicalLabels.Count, StringComparer.Ordinal);
        foreach (var label in canonicalLabels)
            counts[label] = 0;

        // Dedup other-text entries by trimmed value across the week.
        var otherSet = new HashSet<string>(StringComparer.Ordinal);
        var otherEntries = new List<string>();

        foreach (var vep in uniqueVeps)
        {
            var chips = pickChips(vep);
            if (chips is null) continue;

            foreach (var chip in chips)
            {
                if (counts.ContainsKey(chip))
                    counts[chip]++;
            }

            if (chips.Contains(DietaryOptions.OtherOption))
            {
                var text = pickOtherText(vep);
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
