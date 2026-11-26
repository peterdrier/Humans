using Humans.Base.Helpers;
using Humans.Governance.Contracts;
using Humans.Users.Contracts;

namespace Humans.Users.Models;

internal static class AdminHumanDetailViewModelBuilder
{
    public static AdminHumanDetailViewModel Build(
        UserInfo info,
        MembershipTier membershipTier,
        IReadOnlyList<UserEmailRowSnapshot> userEmails,
        int consentCount)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(userEmails);

        var profile = info.Profile;

        return new AdminHumanDetailViewModel
        {
            UserId = info.Id,
            Email = info.Email ?? string.Empty,
            DisplayName = info.BurnerName,
            CreatedAt = info.CreatedAt.ToDateTimeUtc(),
            LastLoginAt = info.LastLoginAt?.ToDateTimeUtc(),
            State = info.State,
            AdminNotes = profile?.AdminNotes,
            PreferredLanguage = info.PreferredLanguage,
            MembershipTier = membershipTier,
            ConsentCount = consentCount,
            Languages = (profile?.Languages ?? [])
                .OrderByDescending(pl => pl.Proficiency)
                .ThenBy(pl => pl.LanguageCode, StringComparer.OrdinalIgnoreCase)
                .Select(pl => new ProfileLanguageDisplayViewModel
                {
                    LanguageName = LanguageCatalog.GetDisplayName(pl.LanguageCode),
                    Proficiency = pl.Proficiency
                }).ToList(),
            UserEmails = userEmails
                .OrderBy(e => e.Email, StringComparer.OrdinalIgnoreCase)
                .Select(e => new AdminUserEmailViewModel
                {
                    Email = e.Email,
                    IsVerified = e.IsVerified,
                    IsPrimary = e.IsPrimary,
                    Visibility = e.Visibility,
                }).ToList(),
        };
    }
}
