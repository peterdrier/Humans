using Humans.Teams.Contracts;
using Humans.Base.Helpers;
using Humans.Camps.Contracts;
using Humans.Governance.Contracts;
using Humans.Users.Contracts;

namespace Humans.Users.Models;

internal static class ProfileSummaryViewModelBuilder
{
    public static ProfileSummaryViewModel BuildWithProfile(
        UserInfo info,
        IReadOnlyList<TeamMembership> memberships,
        MembershipTier tier,
        bool isApproved,
        CampUserInfo? camp = null)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(memberships);

        var profile = info.Profile
            ?? throw new ArgumentException("UserInfo.Profile must be non-null.", nameof(info));

        var orderedMemberships = memberships
            .OrderBy(m => m.TeamName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ProfileSummaryViewModel
        {
            UserId = info.Id,
            DisplayName = info.BurnerName,
            Email = info.Email,
            ProfilePictureUrl = info.ProfilePictureUrl,
            PreferredLanguage = info.PreferredLanguage,
            MembershipTier = tier.ToString(),
            MembershipStatus = info.IsSuspended ? "Suspended"
                : isApproved ? "Active" : "Pending",
            City = profile.City,
            CountryCode = profile.CountryCode,
            IsSuspended = info.IsSuspended,
            Teams = orderedMemberships.Where(m => !m.IsHidden).Select(m => m.TeamName).ToList(),
            HiddenTeams = orderedMemberships.Where(m => m.IsHidden).Select(m => m.TeamName).ToList(),
            Languages = profile.Languages.Select(pl => new ProfileLanguageDisplayViewModel
            {
                LanguageCode = pl.LanguageCode,
                LanguageName = LanguageCatalog.GetDisplayName(pl.LanguageCode),
                Proficiency = pl.Proficiency
            }).ToList(),
            CampName = camp?.Season?.Name,
            CampRoles = camp?.Roles ?? []
        };
    }
}
