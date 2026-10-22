namespace Humans.Users.Contracts;

/// <summary>
/// Out of 20 points: names alone land in the low double digits, and the picture and Burner CV move the bar
/// visibly. Shift-tag preferences live in another table, so they are passed in.
/// </summary>
public static class ProfileCompletion
{
    public static int ComputePercent(ProfileInfo? profile, bool hasShiftTagPreferences)
    {
        if (profile is null) return 0;

        var checks = new (bool Filled, int Weight)[]
        {
            // 3 points get a member past zero on first arrival at Home.
            (!string.IsNullOrWhiteSpace(profile.BurnerName), 1),
            (!string.IsNullOrWhiteSpace(profile.FirstName), 1),
            (!string.IsNullOrWhiteSpace(profile.LastName), 1),

            // The single biggest visible enrichment, so the heaviest weight.
            (profile.HasCustomPicture, 5),

            (!string.IsNullOrWhiteSpace(profile.Pronouns), 1),
            (!string.IsNullOrWhiteSpace(profile.Bio), 2),
            (!string.IsNullOrWhiteSpace(profile.City) && !string.IsNullOrWhiteSpace(profile.CountryCode), 1),
            (profile.BirthdayDay.HasValue && profile.BirthdayMonth.HasValue, 1),

            (!string.IsNullOrWhiteSpace(profile.ContributionInterests), 2),

            // The form fills them together, so any one counts.
            (!string.IsNullOrWhiteSpace(profile.EmergencyContactName)
                || !string.IsNullOrWhiteSpace(profile.EmergencyContactPhone)
                || !string.IsNullOrWhiteSpace(profile.EmergencyContactRelationship), 1),

            (profile.Languages.Count > 0, 1),

            // The explicit "no prior burn experience" flag counts as done.
            (profile.VolunteerHistory.Count > 0 || profile.NoPriorBurnExperience, 2),

            (hasShiftTagPreferences, 1),
        };

        var earned = checks.Where(c => c.Filled).Sum(c => c.Weight);
        var total = checks.Sum(c => c.Weight);
        return (int)Math.Round(100.0 * earned / total);
    }
}
