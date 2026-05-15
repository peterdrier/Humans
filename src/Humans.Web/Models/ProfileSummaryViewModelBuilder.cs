using Humans.Application.DTOs;
using Humans.Application.Models;
using Humans.Application.Interfaces.Profiles;
using Humans.Domain.Entities;
using ProfileEntity = Humans.Domain.Entities.Profile;

namespace Humans.Web.Models;

public static class ProfileSummaryViewModelBuilder
{
    public static ProfileSummaryViewModel BuildWithoutProfile(
        User user,
        IReadOnlyList<UserEmailEditDto> userEmails)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(userEmails);

        var fallbackEmail = userEmails.FirstOrDefault(e => e.IsVerified && e.IsPrimary)?.Email
            ?? userEmails.FirstOrDefault(e => e.IsVerified)?.Email;

        return new ProfileSummaryViewModel
        {
            UserId = user.Id,
            DisplayName = user.DisplayName,
            Email = fallbackEmail,
            ProfilePictureUrl = user.ProfilePictureUrl,
            PreferredLanguage = user.PreferredLanguage,
            HasProfile = false,
        };
    }

    public static ProfileSummaryViewModel BuildWithProfile(
        User user,
        ProfileEntity profile,
        IReadOnlyList<TeamMembership> memberships,
        IReadOnlyList<ProfileLanguageSnapshot> profileLanguages,
        Func<ProfileEntity, string?> customPictureUrl)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(profileLanguages);
        ArgumentNullException.ThrowIfNull(customPictureUrl);

        var orderedMemberships = memberships
            .OrderBy(m => m.TeamName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ProfileSummaryViewModel
        {
            UserId = user.Id,
            DisplayName = user.DisplayName,
            Email = user.Email,
            ProfilePictureUrl = profile.HasCustomProfilePicture
                ? customPictureUrl(profile)
                : user.ProfilePictureUrl,
            PreferredLanguage = user.PreferredLanguage,
            MembershipTier = profile.MembershipTier.ToString(),
            MembershipStatus = profile.IsSuspended ? "Suspended"
                : profile.IsApproved ? "Active" : "Pending",
            City = profile.City,
            CountryCode = profile.CountryCode,
            IsSuspended = profile.IsSuspended,
            Teams = orderedMemberships.Where(m => !m.IsHidden).Select(m => m.TeamName).ToList(),
            HiddenTeams = orderedMemberships.Where(m => m.IsHidden).Select(m => m.TeamName).ToList(),
            Languages = profileLanguages.Select(pl => new ProfileLanguageDisplayViewModel
            {
                LanguageCode = pl.LanguageCode,
                LanguageName = Helpers.LanguageCatalog.GetDisplayName(pl.LanguageCode),
                Proficiency = pl.Proficiency
            }).ToList()
        };
    }
}
