using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Humans.Application.Helpers;
using Humans.Domain.Enums;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Profiles;

namespace Humans.Application.Services.GoogleIntegration;

/// <summary>4-step @nobodies.team email provisioning flow. Used by HumanController (Admin) and TeamAdminController (Coordinators).</summary>
public sealed class EmailProvisioningService(
    IUserService userService,
    IGoogleWorkspaceUserService workspaceUserService,
    IUserEmailService userEmailService,
    ITeamServiceRead teamService,
    IEmailService emailService,
    INotificationService notificationService,
    IAuditLogService auditLogService,
    ILogger<EmailProvisioningService> logger) : IEmailProvisioningService
{
    public async Task<EmailProvisioningResult> ProvisionNobodiesEmailAsync(
        Guid userId,
        string emailPrefix,
        Guid provisionedByUserId)
    {
        var user = await userService.GetUserInfoAsync(userId);
        if (user is null)
            return new EmailProvisioningResult(false, ErrorMessage: "User not found.");

        var sanitizedPrefix = SanitizeEmailPrefix(emailPrefix);
        if (string.IsNullOrEmpty(sanitizedPrefix))
            return new EmailProvisioningResult(false, ErrorMessage: "Email prefix contains characters that cannot be transliterated to ASCII. Please choose a different prefix.");

        var fullEmail = $"{sanitizedPrefix}@nobodies.team";

        try
        {
            // DB conflict check BEFORE Workspace check — prevents stale Workspace accounts from silently re-binding identity.
            // user_emails is the single source of truth (#687).
            var conflictingEmailUserId =
                await userEmailService.GetOtherUserIdHavingEmailAsync(fullEmail, userId);
            if (conflictingEmailUserId is not null)
            {
                logger.LogWarning(
                    "Provisioning rejected: {Email} is already linked to user {ConflictUserId} (requested for {UserId})",
                    fullEmail, conflictingEmailUserId, userId);
                return new EmailProvisioningResult(false, fullEmail,
                    ErrorMessage: $"{fullEmail} is already in use by another human.");
            }

            // Reject if the prefix collides with a team's Google Group. Team groups
            // live on the same domain (@nobodies.team), so a user account with the
            // same address would cause mail-routing chaos and break group membership.
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

            // Check if account already exists in Google Workspace
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
            // 1. Capture recovery (personal) email  2. Provision Workspace  3. Link @nobodies.team  4. Send creds to recovery

            // Step 1: Capture recovery email BEFORE the notification target changes.
            var recoveryEmail = await ResolveRecoveryEmailAsync(userId, user.Email);

            // Step 2: Generate temp password and provision in Google Workspace.
            var tempPassword = PasswordGenerator.GenerateTemporary();
            await workspaceUserService.ProvisionAccountAsync(
                fullEmail, firstName, lastName, tempPassword,
                recoveryEmail);

            // Step 3: Link the email — flips notification target; orchestrator stamps IsGoogle (#687). Do NOT move above step 1.
            await userEmailService.AddVerifiedEmailAsync(userId, fullEmail);

            // Half-completed-prior-provisioning recovery: if an unverified row predated this call, verify + stamp IsGoogle explicitly.
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
            if (workspaceRow is not null && !workspaceRow.IsGoogle)
            {
                await userEmailService.SetGoogleAsync(userId, workspaceRow.Id, provisionedByUserId);
            }

            // Audit
            await auditLogService.LogAsync(
                AuditAction.WorkspaceAccountProvisioned,
                "WorkspaceAccount", userId,
                $"Provisioned and linked @nobodies.team account: {fullEmail}",
                provisionedByUserId);

            // Step 4: Send credentials to the PERSONAL email captured in step 1.
            if (!string.IsNullOrEmpty(recoveryEmail))
            {
                await emailService.SendWorkspaceCredentialsAsync(
                    recoveryEmail, user.BurnerName, fullEmail, tempPassword,
                    user.PreferredLanguage);
            }

            // In-app notification (best-effort)
            try
            {
                await notificationService.SendAsync(
                    NotificationSource.WorkspaceCredentialsReady,
                    NotificationClass.Informational,
                    NotificationPriority.Normal,
                    "Your @nobodies.team account is ready",
                    [userId],
                    body: $"Your workspace email {fullEmail} has been provisioned. Check your personal email for login credentials.",
                    actionUrl: "/Profile",
                    actionLabel: "View profile");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to dispatch WorkspaceCredentialsReady notification for user {UserId}", userId);
            }

            return !string.IsNullOrEmpty(recoveryEmail)
                ? new EmailProvisioningResult(true, fullEmail, recoveryEmail)
                : new EmailProvisioningResult(true, fullEmail);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to provision @nobodies.team account {Email} for user {UserId}", fullEmail, userId);
            return new EmailProvisioningResult(false, fullEmail, ErrorMessage: $"Failed to provision {fullEmail}. Check logs for details.");
        }
    }

    /// <summary>
    /// Returns the verified notification-target email, falling back to OAuth email. Never returns @nobodies.team (user can't yet reach it).
    /// </summary>
    private async Task<string?> ResolveRecoveryEmailAsync(Guid userId, string? oauthEmail)
    {
        var emails = await userEmailService.GetUserEmailsAsync(userId);
        var target = emails.FirstOrDefault(e => e.IsPrimary && e.IsVerified);
        var candidate = target?.Email ?? oauthEmail;

        if (candidate?.EndsWith("@nobodies.team", StringComparison.OrdinalIgnoreCase) == true)
            return oauthEmail;

        return candidate;
    }

    /// <summary>
    /// Transliterates to ASCII: German maps (ü→ue, ö→oe, ä→ae, ß→ss) then NFD-strip diacritics. Null on invalid local-part chars; empty on blank input.
    /// </summary>
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

        // NFD decomposition: split base characters from combining marks, then strip marks
        var normalized = sb.ToString().Normalize(NormalizationForm.FormD);
        var result = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                result.Append(ch);
        }

        var ascii = result.ToString().ToLowerInvariant();

        // Validate result contains only valid ASCII email local-part characters
        foreach (var ch in ascii)
        {
            if (ch > 127 || ch <= ' ' || ch == 0x7F)
                return null;
        }

        return ascii;
    }
}
