using Humans.Application;
using Humans.Domain.Enums;
using Humans.Domain.Helpers;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Campaigns;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Profiles;
using NodaTime;

namespace Humans.Web.Models;

public static class AdminHumanDetailViewModelBuilder
{
    public static AdminHumanDetailViewModel Build(
        UserInfo info,
        IReadOnlyList<UserApplicationSnapshot> applications,
        IReadOnlyList<UserEmailRowSnapshot> userEmails,
        int consentCount,
        IReadOnlyList<RoleAssignmentSummarySnapshot> roleAssignments,
        IReadOnlyDictionary<Guid, string> roleCreatorNamesByUserId,
        IReadOnlyList<CampaignGrantSummary> campaignGrants,
        int outboxCount,
        Instant now,
        string? rejectedByName,
        string? revealedIban)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(applications);
        ArgumentNullException.ThrowIfNull(userEmails);
        ArgumentNullException.ThrowIfNull(roleAssignments);
        ArgumentNullException.ThrowIfNull(roleCreatorNamesByUserId);
        ArgumentNullException.ThrowIfNull(campaignGrants);

        var profile = info.Profile;

        var effectiveEmail = userEmails
            .FirstOrDefault(e => e.IsPrimary && e.IsVerified)?.Email
            ?? info.Email;

        return new AdminHumanDetailViewModel
        {
            UserId = info.Id,
            Email = effectiveEmail ?? string.Empty,
            DisplayName = info.DisplayName,
            ProfilePictureUrl = info.ProfilePictureUrl,
            CreatedAt = info.CreatedAt.ToDateTimeUtc(),
            LastLoginAt = info.LastLoginAt?.ToDateTimeUtc(),
            IsSuspended = info.IsSuspended,
            IsApproved = profile?.IsApproved ?? false,
            HasProfile = profile is not null,
            AdminNotes = profile?.AdminNotes,
            PreferredLanguage = info.PreferredLanguage,
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
            Languages = (profile?.Languages ?? []).Select(pl => new ProfileLanguageDisplayViewModel
            {
                LanguageCode = pl.LanguageCode,
                LanguageName = Helpers.LanguageCatalog.GetDisplayName(pl.LanguageCode),
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
            MaskedIban = string.IsNullOrEmpty(profile?.Iban)
                ? null
                : IbanFormatter.Mask(profile.Iban),
            RevealedIban = revealedIban,
        };
    }

    private static string? GetRoleCreatorName(IReadOnlyDictionary<Guid, string> namesByUserId, RoleAssignmentSummarySnapshot roleAssignment) =>
        namesByUserId.TryGetValue(roleAssignment.CreatedByUserId, out var name) ? name : null;
}
