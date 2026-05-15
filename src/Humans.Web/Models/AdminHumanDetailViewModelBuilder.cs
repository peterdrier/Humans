using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Domain.Helpers;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Campaigns;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Profiles;
using NodaTime;
using ProfileEntity = Humans.Domain.Entities.Profile;

namespace Humans.Web.Models;

public static class AdminHumanDetailViewModelBuilder
{
    public static AdminHumanDetailViewModel Build(
        User user,
        ProfileEntity? profile,
        IReadOnlyList<UserApplicationSnapshot> applications,
        IReadOnlyList<UserEmailRowSnapshot> userEmails,
        int consentCount,
        IReadOnlyList<RoleAssignmentSummarySnapshot> roleAssignments,
        IReadOnlyDictionary<Guid, string> roleCreatorNamesByUserId,
        IReadOnlyList<ProfileLanguageSnapshot> profileLanguages,
        IReadOnlyList<CampaignGrantSummary> campaignGrants,
        int outboxCount,
        Instant now,
        string? rejectedByName,
        string? revealedIban)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(applications);
        ArgumentNullException.ThrowIfNull(userEmails);
        ArgumentNullException.ThrowIfNull(roleAssignments);
        ArgumentNullException.ThrowIfNull(roleCreatorNamesByUserId);
        ArgumentNullException.ThrowIfNull(profileLanguages);
        ArgumentNullException.ThrowIfNull(campaignGrants);

        var effectiveEmail = userEmails
            .FirstOrDefault(e => e.IsPrimary && e.IsVerified)?.Email
            ?? user.Email;

        return new AdminHumanDetailViewModel
        {
            UserId = user.Id,
            Email = effectiveEmail ?? string.Empty,
            DisplayName = user.DisplayName,
            ProfilePictureUrl = user.ProfilePictureUrl,
            CreatedAt = user.CreatedAt.ToDateTimeUtc(),
            LastLoginAt = user.LastLoginAt?.ToDateTimeUtc(),
            IsSuspended = profile?.IsSuspended ?? false,
            IsApproved = profile?.IsApproved ?? false,
            HasProfile = profile is not null,
            AdminNotes = profile?.AdminNotes,
            PreferredLanguage = user.PreferredLanguage,
            MembershipTier = profile?.MembershipTier ?? MembershipTier.Volunteer,
            ConsentCheckStatus = profile?.ConsentCheckStatus,
            IsRejected = profile?.RejectedAt is not null,
            RejectionReason = profile?.RejectionReason,
            RejectedAt = profile?.RejectedAt?.ToDateTimeUtc(),
            RejectedByName = rejectedByName,
            ApplicationCount = applications.Count,
            ConsentCount = consentCount,
            CampaignGrants = campaignGrants,
            OutboxCount = outboxCount,
            Applications = applications
                .OrderByDescending(a => a.SubmittedAt)
                .Take(5)
                .Select(a => new AdminHumanApplicationViewModel
                {
                    Id = a.Id,
                    Status = a.Status,
                    SubmittedAt = a.SubmittedAt.ToDateTimeUtc()
                }).ToList(),
            RoleAssignments = roleAssignments.Select(ra => new AdminRoleAssignmentViewModel
            {
                Id = ra.Id,
                UserId = ra.UserId,
                RoleName = ra.RoleName,
                ValidFrom = ra.ValidFrom.ToDateTimeUtc(),
                ValidTo = ra.ValidTo?.ToDateTimeUtc(),
                Notes = ra.Notes,
                IsActive = ra.IsActive(now),
                CreatedByName = GetRoleCreatorName(roleCreatorNamesByUserId, ra),
                CreatedAt = ra.CreatedAt.ToDateTimeUtc()
            }).ToList(),
            Languages = profileLanguages.Select(pl => new ProfileLanguageDisplayViewModel
            {
                LanguageCode = pl.LanguageCode,
                LanguageName = Helpers.LanguageCatalog.GetDisplayName(pl.LanguageCode),
                Proficiency = pl.Proficiency
            }).ToList(),
            OAuthEmail = user.Email,
            GoogleServiceEmail = userEmails
                .Where(e => e.IsVerified && e.IsGoogle)
                .Select(e => e.Email)
                .FirstOrDefault()
                ?? user.Email,
            GoogleEmailStatus = user.GoogleEmailStatus,
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
            MaskedIban = string.IsNullOrEmpty(profile?.Iban)
                ? null
                : IbanFormatter.Mask(profile.Iban),
            RevealedIban = revealedIban,
        };
    }

    private static string? GetRoleCreatorName(IReadOnlyDictionary<Guid, string> namesByUserId, RoleAssignmentSummarySnapshot roleAssignment) =>
        namesByUserId.TryGetValue(roleAssignment.CreatedByUserId, out var name) ? name : null;
}
