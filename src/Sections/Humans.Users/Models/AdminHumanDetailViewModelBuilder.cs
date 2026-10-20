using Humans.Base.Helpers;
using Humans.Governance.Contracts;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Users.Models;

internal static class AdminHumanDetailViewModelBuilder
{
    public static AdminHumanDetailViewModel Build(
        UserInfo info,
        IReadOnlyList<UserApplicationSnapshot> applications,
        IReadOnlyList<UserEmailRowSnapshot> userEmails,
        int consentCount,
        Instant now,
        string? rejectedByName)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(applications);
        ArgumentNullException.ThrowIfNull(userEmails);

        var profile = info.Profile;

        var effectiveEmail = userEmails
            .FirstOrDefault(e => e.IsPrimary && e.IsVerified)?.Email
            ?? info.Email;

        return new AdminHumanDetailViewModel
        {
            UserId = info.Id,
            Email = effectiveEmail ?? string.Empty,
            DisplayName = info.BurnerName,
            ProfilePictureUrl = info.ProfilePictureUrl,
            CreatedAt = info.CreatedAt.ToDateTimeUtc(),
            LastLoginAt = info.LastLoginAt?.ToDateTimeUtc(),
            State = info.State,
            AdminNotes = profile?.AdminNotes,
            PreferredLanguage = info.PreferredLanguage,
            MembershipTier = profile?.MembershipTier ?? MembershipTier.Volunteer,
            ConsentCheckStatus = profile?.ConsentCheckStatus,
            RejectionReason = profile?.RejectionReason,
            RejectedAt = profile?.RejectedAt?.ToDateTimeUtc(),
            RejectedByName = rejectedByName,
            ApplicationCount = applications.Count,
            ConsentCount = consentCount,
            Applications = applications
                .OrderByDescending(a => a.SubmittedAt)
                .Take(5)
                .Select(a => new AdminHumanApplicationViewModel
                {
                    Id = a.Id,
                    Status = a.Status,
                    SubmittedAt = a.SubmittedAt.ToDateTimeUtc()
                }).ToList(),
            Languages = (profile?.Languages ?? []).Select(pl => new ProfileLanguageDisplayViewModel
            {
                LanguageCode = pl.LanguageCode,
                LanguageName = LanguageCatalog.GetDisplayName(pl.LanguageCode),
                Proficiency = pl.Proficiency
            }).ToList(),
            OAuthEmail = info.Email,
            GoogleServiceEmail = userEmails
                .Where(e => e.IsVerified && e.IsGoogle)
                .Select(e => e.Email)
                .FirstOrDefault()
                ?? info.Email,
            GoogleEmailStatus = info.GoogleEmailStatus,
            UserEmails = userEmails
                .OrderBy(e => e.Email, StringComparer.OrdinalIgnoreCase)
                .Select(e => new AdminUserEmailViewModel
                {
                    Email = e.Email,
                    IsGoogle = e.IsGoogle,
                    IsVerified = e.IsVerified,
                    IsPrimary = e.IsPrimary,
                    Visibility = e.Visibility,
                }).ToList(),
        };
    }
}
