using Humans.Application.Interfaces.Cantina;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Cantina.Dtos;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using ProfileEntity = Humans.Domain.Entities.Profile;

namespace Humans.Application.Services.Cantina;

/// <summary>
/// Application-layer implementation of <see cref="ICantinaRosterService"/>.
/// Pulls the on-site cohort and their <c>VolunteerEventProfile</c> rows
/// from <c>IShiftManagementRepository</c>, stitches burner-name labels
/// from <c>IProfileService</c> / <c>IUserService</c>, and computes the
/// aggregates the Cantina coordinator UI needs. Medical fields never
/// leave the service — they are not present on any DTO.
/// </summary>
public sealed class CantinaRosterService : ICantinaRosterService
{
    private readonly IShiftManagementRepository _shiftRepo;
    private readonly IProfileService _profileService;
    private readonly IUserService _userService;

    // Canonical preference labels, with the "Unanswered" pseudo-bucket.
    private static readonly string UnansweredKey = "Unanswered";

    public CantinaRosterService(
        IShiftManagementRepository shiftRepo,
        IProfileService profileService,
        IUserService userService)
    {
        _shiftRepo = shiftRepo;
        _profileService = profileService;
        _userService = userService;
    }

    public async Task<DailyRosterDto> GetDailyRosterAsync(int dayOffset, CancellationToken ct = default)
    {
        var eventSettings = await _shiftRepo.GetActiveEventSettingsAsync(ct).ConfigureAwait(false);
        var calendarDate = eventSettings is null
            ? (NodaTime.LocalDate?)null
            : eventSettings.GateOpeningDate.PlusDays(dayOffset);
        var eventName = eventSettings?.EventName;

        var onSiteUserIds = await _shiftRepo.GetOnSiteUserIdsForDayAsync(dayOffset, ct).ConfigureAwait(false);
        if (onSiteUserIds.Count == 0)
        {
            return new DailyRosterDto(
                dayOffset,
                calendarDate,
                eventName,
                TotalOnSite: 0,
                UnansweredCount: 0,
                DietaryBreakdown: EmptyDietaryBreakdown(),
                AllergyRollup: EmptyRollup(DietaryOptions.AllergyOptions),
                AllergyOtherEntries: Array.Empty<string>(),
                IntoleranceRollup: EmptyRollup(DietaryOptions.IntoleranceOptions),
                IntoleranceOtherEntries: Array.Empty<string>(),
                People: Array.Empty<RosterPersonDto>());
        }

        var veps = await _shiftRepo.GetOnSiteVolunteerProfilesForDayAsync(dayOffset, ct).ConfigureAwait(false);
        var profiles = await _profileService.GetByUserIdsAsync(onSiteUserIds, ct).ConfigureAwait(false);
        var users = await _userService.GetByIdsAsync(onSiteUserIds, ct).ConfigureAwait(false);

        var vepByUserId = veps.ToDictionary(v => v.UserId);

        var people = onSiteUserIds
            .Select(id =>
            {
                profiles.TryGetValue(id, out var profile);
                users.TryGetValue(id, out var user);
                vepByUserId.TryGetValue(id, out var vep);
                return new RosterPersonDto(
                    UserId: id,
                    BurnerName: ResolveBurnerName(profile, user),
                    DietaryPreference: vep?.DietaryPreference,
                    Allergies: vep?.Allergies is { Count: > 0 } a ? a.ToArray() : Array.Empty<string>(),
                    AllergyOtherText: vep?.AllergyOtherText,
                    Intolerances: vep?.Intolerances is { Count: > 0 } i ? i.ToArray() : Array.Empty<string>(),
                    IntoleranceOtherText: vep?.IntoleranceOtherText);
            })
            .OrderBy(p => p.BurnerName, StringComparer.Ordinal)
            .ToList();

        var dietaryBreakdown = BuildDietaryBreakdown(veps, onSiteUserIds.Count);
        var (allergyRollup, allergyOther) = BuildRollup(
            veps,
            vep => vep.Allergies,
            vep => vep.AllergyOtherText,
            DietaryOptions.AllergyOptions);
        var (intoleranceRollup, intoleranceOther) = BuildRollup(
            veps,
            vep => vep.Intolerances,
            vep => vep.IntoleranceOtherText,
            DietaryOptions.IntoleranceOptions);

        // "Unanswered" = on-site humans whose DietaryPreference is null/empty,
        // which includes both "no VEP at all" and "VEP exists but DietaryPreference
        // is blank". See daily-roster.md.
        var answeredCount = veps.Count(v => !string.IsNullOrEmpty(v.DietaryPreference));
        var unanswered = onSiteUserIds.Count - answeredCount;

        return new DailyRosterDto(
            dayOffset,
            calendarDate,
            eventName,
            TotalOnSite: onSiteUserIds.Count,
            UnansweredCount: unanswered,
            DietaryBreakdown: dietaryBreakdown,
            AllergyRollup: allergyRollup,
            AllergyOtherEntries: allergyOther,
            IntoleranceRollup: intoleranceRollup,
            IntoleranceOtherEntries: intoleranceOther,
            People: people);
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
        IReadOnlyList<VolunteerEventProfile> veps, int totalOnSite)
    {
        var dict = new Dictionary<string, int>(DietaryOptions.DietaryPreferences.Count + 1, StringComparer.Ordinal);
        foreach (var pref in DietaryOptions.DietaryPreferences)
            dict[pref] = 0;

        var answered = 0;
        foreach (var vep in veps)
        {
            if (string.IsNullOrEmpty(vep.DietaryPreference))
                continue;

            answered++;
            // Only bucket known preferences — unknown/legacy values would otherwise
            // distort the breakdown. Treat them as Unanswered for display purposes.
            if (dict.ContainsKey(vep.DietaryPreference))
                dict[vep.DietaryPreference]++;
        }

        dict[UnansweredKey] = totalOnSite - answered;
        return dict;
    }

    private static (IReadOnlyList<RollupItemDto> Rollup, IReadOnlyList<string> OtherEntries) BuildRollup(
        IReadOnlyList<VolunteerEventProfile> veps,
        Func<VolunteerEventProfile, List<string>> pickChips,
        Func<VolunteerEventProfile, string?> pickOtherText,
        IReadOnlyList<string> canonicalLabels)
    {
        var counts = new Dictionary<string, int>(canonicalLabels.Count, StringComparer.Ordinal);
        foreach (var label in canonicalLabels)
            counts[label] = 0;

        var otherEntries = new List<string>();

        foreach (var vep in veps)
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
                    otherEntries.Add(text.Trim());
            }
        }

        // Preserve canonical ordering of the rollup rows.
        var rollup = new List<RollupItemDto>(canonicalLabels.Count);
        foreach (var label in canonicalLabels)
            rollup.Add(new RollupItemDto(label, counts[label]));

        return (rollup, otherEntries);
    }
}
