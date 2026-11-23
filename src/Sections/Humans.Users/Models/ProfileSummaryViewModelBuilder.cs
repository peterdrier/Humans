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
        bool isBoardMember,
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
            Email = info.Email,
            PreferredLanguage = info.PreferredLanguage,
            MembershipTier = tier.ToString(),
            IsBoardMember = isBoardMember,
            MembershipStatus = info.IsSuspended ? "Suspended"
                : info.State == UserState.Active ? "Active" : "Pending",
            City = profile.City,
            CountryCode = profile.CountryCode,
            IsSuspended = info.IsSuspended,
            Teams = orderedMemberships.Where(m => !m.IsHidden).Select(m => m.TeamName).ToList(),
            HiddenTeams = orderedMemberships.Where(m => m.IsHidden).Select(m => m.TeamName).ToList(),
            Languages = profile.Languages
                .OrderByDescending(pl => pl.Proficiency)
                .ThenBy(pl => pl.LanguageCode, StringComparer.OrdinalIgnoreCase)
                .Select(pl => new ProfileLanguageDisplayViewModel
                {
                    LanguageName = LanguageCatalog.GetDisplayName(pl.LanguageCode),
                    Proficiency = pl.Proficiency
                }).ToList(),
            CampName = camp?.Season?.Name,
            CampRoles = camp?.Roles
                .OrderBy(role => role.SortOrder)
                .ThenBy(role => role.Name, StringComparer.OrdinalIgnoreCase)
                .Select(role => role.Name)
                .ToList() ?? []
        };
    }
}
