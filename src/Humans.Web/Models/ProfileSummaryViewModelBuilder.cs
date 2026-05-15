using Humans.Application;
using Humans.Application.Models;

namespace Humans.Web.Models;

public static class ProfileSummaryViewModelBuilder
{
    public static ProfileSummaryViewModel BuildWithoutProfile(UserInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        // Email is intentionally NOT populated here — the popover surfaces this
        // model to any authenticated user, so exposing email (verified or not)
        // is a GDPR PII leak. Admins who need email use /Profile/{id}/Admin.
        return new ProfileSummaryViewModel
        {
            UserId = info.Id,
            DisplayName = info.DisplayName,
            ProfilePictureUrl = info.ProfilePictureUrl,
            PreferredLanguage = info.PreferredLanguage,
            HasProfile = false,
        };
    }

    public static ProfileSummaryViewModel BuildWithProfile(
        UserInfo info,
        IReadOnlyList<TeamMembership> memberships,
        Func<ProfileInfo, string?> customPictureUrl)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(customPictureUrl);

        var profile = info.Profile
            ?? throw new ArgumentException("UserInfo.Profile must be non-null.", nameof(info));

        var orderedMemberships = memberships
            .OrderBy(m => m.TeamName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ProfileSummaryViewModel
        {
            UserId = info.Id,
            DisplayName = info.DisplayName,
            Email = info.Email,
            ProfilePictureUrl = profile.HasCustomPicture
                ? customPictureUrl(profile)
                : info.ProfilePictureUrl,
            PreferredLanguage = info.PreferredLanguage,
            MembershipTier = profile.MembershipTier.ToString(),
            MembershipStatus = info.IsSuspended ? "Suspended"
                : profile.IsApproved ? "Active" : "Pending",
            City = profile.City,
            CountryCode = profile.CountryCode,
            IsSuspended = info.IsSuspended,
            Teams = orderedMemberships.Where(m => !m.IsHidden).Select(m => m.TeamName).ToList(),
            HiddenTeams = orderedMemberships.Where(m => m.IsHidden).Select(m => m.TeamName).ToList(),
            Languages = profile.Languages.Select(pl => new ProfileLanguageDisplayViewModel
            {
                LanguageCode = pl.LanguageCode,
                LanguageName = Helpers.LanguageCatalog.GetDisplayName(pl.LanguageCode),
                Proficiency = pl.Proficiency
            }).ToList()
        };
    }
}
