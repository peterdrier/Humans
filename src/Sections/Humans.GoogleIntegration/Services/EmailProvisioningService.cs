using Humans.GoogleIntegration.Contracts;
using System.Globalization;
using System.Resources;
using System.Net.Mail;
using Humans.Base.Extensions;
using System.Text;
using Humans.Base.Helpers;
using Humans.AuditLog.Contracts;
using Humans.Email.Contracts;
using Humans.Teams.Contracts;
using Humans.Notifications.Contracts;
using Humans.Users.Contracts;

namespace Humans.GoogleIntegration.Services;

internal sealed class EmailProvisioningService(
    IUserService userService,
    IGoogleWorkspaceUserService workspaceUserService,
    IUserEmailService userEmailService,
    IWorkspaceIdentityManagement workspaceIdentities,
    ITeamService teamService,
    IEmailService emailService,
    GoogleIntegrationEmails emailMessages,
    INotificationEmitter notificationService,
    IAuditLogService auditLogService,
    ILogger<EmailProvisioningService> logger) : IEmailProvisioningService
{
    private static readonly ResourceManager NoticeResources = new(typeof(GoogleIntegrationResource));
    internal const string InvalidEmailPrefixMessage = "Enter a valid Workspace username of at most 64 characters. Don't include @, reserved names, or leading, trailing or repeated periods.";

    public async Task<EmailProvisioningResult> ProvisionNobodiesEmailAsync(
        Guid userId,
        string emailPrefix,
        Guid provisionedByUserId)
    {
        // userId comes from a form, so an archived id must never reach Workspace: GetUserInfoAsync resolves a
        // merged-away id to its survivor, and a GDPR tombstone keeps an anonymized name that passes the checks below.
        var user = await userService.GetUserInfoAsync(userId);
        if (user is not { IsActive: true } || user.Id != userId)
            return new EmailProvisioningResult(false, ErrorMessage: "User not found.");

        var sanitizedPrefix = SanitizeEmailPrefix(emailPrefix);
        if (string.IsNullOrEmpty(sanitizedPrefix))
        {
            logger.LogWarning("Workspace provisioning rejected an invalid username for user {UserId}", userId);
            return new EmailProvisioningResult(false, ErrorMessage: InvalidEmailPrefixMessage);
        }

        var fullEmail = $"{sanitizedPrefix}@nobodies.team";

        try
        {
            // DB conflict check before the Workspace check, so a stale Workspace account cannot re-bind the identity.
            var conflictingEmailUserId =
                (await userEmailService.FindByAddressAsync(fullEmail, aliased: false, verifiedOnly: false))
                .FirstOrDefault(r => r.UserId != userId)?.UserId;
            if (conflictingEmailUserId is not null)
            {
                logger.LogWarning(
                    "Provisioning rejected: {Email} is already linked to user {ConflictUserId} (requested for {UserId})",
                    fullEmail, conflictingEmailUserId, userId);
                return new EmailProvisioningResult(false, fullEmail,
                    ErrorMessage: $"{fullEmail} is already in use by another human.");
            }

            // A team Group with this address on the same domain would break mail routing and group membership.
            var conflictingTeamName = (await teamService.GetTeamsAsync()).Values
                .FirstOrDefault(t => string.Equals(t.GoogleGroupPrefix, sanitizedPrefix, StringComparison.OrdinalIgnoreCase))
                ?.Name;
            if (!string.IsNullOrEmpty(conflictingTeamName))
            {
                logger.LogWarning(
                    "Provisioning rejected: {Email} is the Google Group address for team '{TeamName}' (requested for {UserId})",
                    fullEmail, conflictingTeamName, userId);
                return new EmailProvisioningResult(false, fullEmail,
                    ErrorMessage: $"{fullEmail} is the Google Group address for team \"{conflictingTeamName}\" and cannot be used as a personal account.");
            }

            var existing = await workspaceUserService.GetAccountAsync(fullEmail);
            if (existing is not null)
                return new EmailProvisioningResult(false, fullEmail, ErrorMessage: $"Account {fullEmail} already exists in Google Workspace.");

            // Real name from profile, not display/burner name.
            var profile = user.Profile;
            var firstName = profile?.FirstName;
            var lastName = profile?.LastName;
            if (string.IsNullOrWhiteSpace(firstName) || string.IsNullOrWhiteSpace(lastName))
                return new EmailProvisioningResult(false, fullEmail, ErrorMessage: "Cannot provision account: the human must have a first and last name in their profile.");

            // ORDERING IS CRITICAL — do NOT reorder. Recovery email must be captured BEFORE AddVerifiedEmailAsync flips the notification target to @nobodies.team.
            var recoveryEmail = await ResolveRecoveryEmailAsync(userId, user.Email);

            var tempPassword = PasswordGenerator.GenerateTemporary();
            await workspaceUserService.ProvisionAccountAsync(
                fullEmail, firstName, lastName, tempPassword,
                recoveryEmail);

            // Flips the notification target. Do NOT move above the recovery-email capture.
            var emailAddition = await userEmailService.AddVerifiedEmailAsync(userId, fullEmail);
            if (emailAddition.ErrorKey is not null)
                throw new InvalidOperationException("Could not add the verified email address.");

            // Half-completed-prior-provisioning recovery: if an unverified row predated this call, verify it, and designate it explicitly if it is not the identity.
            var rows = await userEmailService.GetUserEmailsAsync(userId);
            var workspaceRow = rows.FirstOrDefault(r =>
                string.Equals(r.Email, fullEmail, StringComparison.OrdinalIgnoreCase));
            if (workspaceRow is not null && !workspaceRow.IsVerified)
            {
                await userEmailService.AdminMarkVerifiedAsync(
                    userId, workspaceRow.Id, provisionedByUserId);
                rows = await userEmailService.GetUserEmailsAsync(userId);
                workspaceRow = rows.FirstOrDefault(r =>
                    string.Equals(r.Email, fullEmail, StringComparison.OrdinalIgnoreCase));
            }
            if (workspaceRow is not null
                && !string.Equals((await workspaceIdentities.GetWorkspaceIdentityAsync(userId))?.Email, workspaceRow.Email,
                    StringComparison.OrdinalIgnoreCase))
            {
                await workspaceIdentities.SetAsync(userId, workspaceRow.Email, provisionedByUserId);
            }

            await auditLogService.LogAsync(
                GoogleIntegrationAuditActions.WorkspaceAccountProvisioned,
                "WorkspaceAccount", userId,
                $"Provisioned and linked @nobodies.team account: {fullEmail}",
                provisionedByUserId);

            var language = user.PreferredLanguage.IsSupportedCultureCode()
                ? user.PreferredLanguage
                : CultureCatalog.DefaultCultureCode;
            var credentialsQueued = false;
            string? warningMessage = null;
            if (!string.IsNullOrEmpty(recoveryEmail))
            {
                try
                {
                    await emailService.SendAsync(emailMessages.WorkspaceCredentials(
                        userId, recoveryEmail, user.BurnerName, fullEmail, tempPassword,
                        language));
                    credentialsQueued = true;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Workspace account created but credentials email handoff failed for user {UserId}", userId);
                    warningMessage = $"Account {fullEmail} provisioned and linked, but the credentials email could not be confirmed. If credentials do not arrive, ask an admin to reset the password.";
                }
            }
            else
            {
                logger.LogWarning("Workspace account created without a credentials recovery address for user {UserId}", userId);
                warningMessage = $"Account {fullEmail} provisioned and linked. No recovery email found; ask an admin to reset the password.";
            }

            try
            {
                var culture = CultureInfo.GetCultureInfo(language);
                await notificationService.SendAsync(
                    GoogleIntegrationNotificationSources.WorkspaceCredentialsReady,
                    NotificationClass.Informational,
                    NotificationPriority.Normal,
                    NoticeResources.GetString("GoogleIntegration_Email_WorkspaceCredentials_Subject", culture)!,
                    [userId],
                    body: string.Format(culture,
                        NoticeResources.GetString(credentialsQueued
                            ? "GoogleIntegration_Notification_WorkspaceReadyBody"
                            : "GoogleIntegration_Notification_WorkspaceReadyWithoutCredentialsBody", culture)!, fullEmail),
                    actionUrl: "/Profile",
                    actionLabel: NoticeResources.GetString("GoogleIntegration_Notification_ViewProfile", culture));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to dispatch WorkspaceCredentialsReady notification for user {UserId}", userId);
            }

            return new EmailProvisioningResult(true, fullEmail, recoveryEmail, WarningMessage: warningMessage);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to provision @nobodies.team account {Email} for user {UserId}", fullEmail, userId);
            return new EmailProvisioningResult(false, fullEmail, ErrorMessage: $"Failed to provision {fullEmail}. Check logs for details.");
        }
    }

    /// <summary>Never returns @nobodies.team: the member cannot reach it yet.</summary>
    private async Task<string?> ResolveRecoveryEmailAsync(Guid userId, string? oauthEmail)
    {
        var emails = await userEmailService.GetUserEmailsAsync(userId);
        var target = emails.FirstOrDefault(e => e.IsPrimary && e.IsVerified);
        var candidate = target?.Email ?? oauthEmail;

        if (candidate?.EndsWith("@nobodies.team", StringComparison.OrdinalIgnoreCase) == true)
            return oauthEmail?.EndsWith("@nobodies.team", StringComparison.OrdinalIgnoreCase) == true
                ? null : oauthEmail;

        return candidate;
    }

    /// <summary>Null for an invalid Workspace username; empty on blank input.</summary>
    internal static string? SanitizeEmailPrefix(string prefix)
    {
        var trimmed = prefix.Trim();

        // German-specific mappings (must be applied before NFD stripping)
        var sb = new StringBuilder(trimmed.Length);
        foreach (var ch in trimmed)
        {
            var mapped = ch switch
            {
                'ü' => "ue",
                'Ü' => "ue",
                'ö' => "oe",
                'Ö' => "oe",
                'ä' => "ae",
                'Ä' => "ae",
                'ß' => "ss",
                _ => null
            };

            if (mapped is not null)
                sb.Append(mapped);
            else
                sb.Append(ch);
        }

        var normalized = sb.ToString().Normalize(NormalizationForm.FormD);
        var result = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                result.Append(ch);
        }

        var ascii = result.ToString().ToLowerInvariant();

        foreach (var ch in ascii)
        {
            if (ch > 127 || ch <= ' ' || ch == 0x7F)
                return null;
        }

        return ascii.Length == 0 || IsValidWorkspaceUsername(ascii) ? ascii : null;
    }

    /// <summary>Checks mailbox syntax and Workspace's primary-username bounds without rewriting the value.</summary>
    internal static bool IsValidWorkspaceUsername(string prefix)
    {
        // https://knowledge.workspace.google.com/admin/users/name-guidelines-for-users-and-groups
        if (string.IsNullOrEmpty(prefix) || prefix.EnumerateRunes().Take(65).Count() > 64
            || prefix.StartsWith('.') || prefix.EndsWith('.') || prefix.Contains("..", StringComparison.Ordinal)
            || string.Equals(prefix, "abuse", StringComparison.OrdinalIgnoreCase)
            || string.Equals(prefix, "postmaster", StringComparison.OrdinalIgnoreCase))
            return false;

        var email = $"{prefix}@nobodies.team";
        return MailAddress.TryCreate(email, out var address)
            && string.Equals(address.Address, email, StringComparison.Ordinal);
    }
}
