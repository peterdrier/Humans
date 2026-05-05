using Humans.Domain.Entities;

namespace Humans.Application.Helpers;

/// <summary>
/// Computes a 0–100 completion percentage for a member profile, used as a
/// nudge on the Home dashboard. Required identity fields (BurnerName,
/// FirstName, LastName) are excluded — by definition any user reaching the
/// Home dashboard already has them. The score reflects optional enrichment
/// fields only.
/// </summary>
public static class ProfileCompletion
{
    /// <summary>
    /// Percentage (0–100) of optional Profile fields that are populated.
    /// Returns 0 for a null profile.
    /// </summary>
    public static int ComputePercent(Profile? profile)
    {
        if (profile is null) return 0;

        var checks = new[]
        {
            !string.IsNullOrWhiteSpace(profile.City) && !string.IsNullOrWhiteSpace(profile.CountryCode),
            !string.IsNullOrWhiteSpace(profile.Bio),
            !string.IsNullOrWhiteSpace(profile.Pronouns),
            !string.IsNullOrWhiteSpace(profile.ContributionInterests),
            profile.DateOfBirth.HasValue,
            !string.IsNullOrWhiteSpace(profile.EmergencyContactName),
            !string.IsNullOrWhiteSpace(profile.EmergencyContactPhone),
            !string.IsNullOrWhiteSpace(profile.EmergencyContactRelationship),
        };

        var filled = checks.Count(c => c);
        return (int)Math.Round(100.0 * filled / checks.Length);
    }
}
