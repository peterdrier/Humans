using Humans.Domain.Entities;

namespace Humans.Application.Helpers;

/// <summary>
/// Computes a 0–100 completion percentage for a member profile, used as a
/// nudge on the Home dashboard. Weighted so a freshly-onboarded user (names
/// only) lands in the low double-digits and the picture / volunteer history
/// move the bar meaningfully when added.
///
/// Total weight = 20 points. Each check below contributes its listed weight
/// when satisfied; the result is rounded to a percentage.
///
/// Caller is responsible for loading the data: <see cref="Profile.VolunteerHistory"/>
/// and <see cref="Profile.Languages"/> must be Include()'d on the entity, and
/// shift-tag preferences live on a separate table so they're passed as a
/// <see cref="ComputePercent(Profile?, bool)"/> argument.
/// </summary>
public static class ProfileCompletion
{
    public static int ComputePercent(Profile? profile) =>
        ComputePercent(profile, hasShiftTagPreferences: false);

    public static int ComputePercent(Profile? profile, bool hasShiftTagPreferences)
    {
        if (profile is null) return 0;

        var checks = new (bool Filled, int Weight)[]
        {
            // Required identity (filled in the onboarding widget Names step) —
            // 3 points get you past zero on first arrival at Home.
            (!string.IsNullOrWhiteSpace(profile.BurnerName), 1),
            (!string.IsNullOrWhiteSpace(profile.FirstName), 1),
            (!string.IsNullOrWhiteSpace(profile.LastName), 1),

            // Profile picture is the single biggest visible enrichment, so it
            // gets the heaviest single weight (~25 % of the bar).
            (profile.HasCustomProfilePicture, 5),

            // Optional identity & narrative
            (!string.IsNullOrWhiteSpace(profile.Pronouns), 1),
            (!string.IsNullOrWhiteSpace(profile.Bio), 2),
            (!string.IsNullOrWhiteSpace(profile.City) && !string.IsNullOrWhiteSpace(profile.CountryCode), 1),
            (profile.DateOfBirth.HasValue, 1),

            // How they want to contribute / what they're up for
            (!string.IsNullOrWhiteSpace(profile.ContributionInterests), 2),

            // Emergency contact — any one of the three populated counts (the
            // edit form fills them together; a single-field nudge is enough).
            (!string.IsNullOrWhiteSpace(profile.EmergencyContactName)
                || !string.IsNullOrWhiteSpace(profile.EmergencyContactPhone)
                || !string.IsNullOrWhiteSpace(profile.EmergencyContactRelationship), 1),

            // Languages spoken (any) — requires .Include(p => p.Languages).
            (profile.Languages.Count > 0, 1),

            // Burner CV — either at least one history entry, OR the explicit
            // "no prior burn experience" flag (treated as completed).
            (profile.VolunteerHistory.Count > 0 || profile.NoPriorBurnExperience, 2),

            // Shift tag preferences (separate table; caller passes a bool).
            (hasShiftTagPreferences, 1),
        };

        var earned = checks.Where(c => c.Filled).Sum(c => c.Weight);
        var total = checks.Sum(c => c.Weight);
        return (int)Math.Round(100.0 * earned / total);
    }

    public static int ComputePercent(ProfileInfo? profile, bool hasShiftTagPreferences)
    {
        if (profile is null) return 0;

        var checks = new (bool Filled, int Weight)[]
        {
            (!string.IsNullOrWhiteSpace(profile.BurnerName), 1),
            (!string.IsNullOrWhiteSpace(profile.FirstName), 1),
            (!string.IsNullOrWhiteSpace(profile.LastName), 1),
            (profile.HasCustomPicture, 5),
            (!string.IsNullOrWhiteSpace(profile.Pronouns), 1),
            (!string.IsNullOrWhiteSpace(profile.Bio), 2),
            (!string.IsNullOrWhiteSpace(profile.City) && !string.IsNullOrWhiteSpace(profile.CountryCode), 1),
            (profile.BirthdayDay.HasValue && profile.BirthdayMonth.HasValue, 1),
            (!string.IsNullOrWhiteSpace(profile.ContributionInterests), 2),
            (!string.IsNullOrWhiteSpace(profile.EmergencyContactName)
                || !string.IsNullOrWhiteSpace(profile.EmergencyContactPhone)
                || !string.IsNullOrWhiteSpace(profile.EmergencyContactRelationship), 1),
            (profile.Languages.Count > 0, 1),
            (profile.VolunteerHistory.Count > 0 || profile.NoPriorBurnExperience, 2),
            (hasShiftTagPreferences, 1),
        };

        var earned = checks.Where(c => c.Filled).Sum(c => c.Weight);
        var total = checks.Sum(c => c.Weight);
        return (int)Math.Round(100.0 * earned / total);
    }
}
