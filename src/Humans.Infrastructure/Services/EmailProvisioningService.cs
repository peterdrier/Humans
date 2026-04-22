using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Humans.Application.Helpers;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;

namespace Humans.Infrastructure.Services;

/// <summary>
/// Encapsulates the 4-step @nobodies.team email provisioning flow.
/// Used by both HumanController (Admin) and TeamAdminController (Coordinators).
/// </summary>
public class EmailProvisioningService : IEmailProvisioningService
{
    private readonly HumansDbContext _dbContext;
    private readonly UserManager<User> _userManager;
    private readonly IGoogleWorkspaceUserService _workspaceUserService;
    private readonly IUserEmailService _userEmailService;
    private readonly IEmailService _emailService;
    private readonly INotificationService _notificationService;
    private readonly IAuditLogService _auditLogService;
    private readonly ILogger<EmailProvisioningService> _logger;

    public EmailProvisioningService(
        HumansDbContext dbContext,
        UserManager<User> userManager,
        IGoogleWorkspaceUserService workspaceUserService,
        IUserEmailService userEmailService,
        IEmailService emailService,
        INotificationService notificationService,
        IAuditLogService auditLogService,
        ILogger<EmailProvisioningService> logger)
    {
        _dbContext = dbContext;
        _userManager = userManager;
        _workspaceUserService = workspaceUserService;
        _userEmailService = userEmailService;
        _emailService = emailService;
        _notificationService = notificationService;
        _auditLogService = auditLogService;
        _logger = logger;
    }

    public async Task<EmailProvisioningResult> ProvisionNobodiesEmailAsync(
        Guid userId,
        string emailPrefix,
        Guid provisionedByUserId)
    {
        var user = await _dbContext.Users
            .Include(u => u.UserEmails)
            .Include(u => u.Profile)
            .FirstOrDefaultAsync(u => u.Id == userId);

        if (user is null)
            return new EmailProvisioningResult(false, ErrorMessage: "User not found.");

        var sanitizedPrefix = SanitizeEmailPrefix(emailPrefix);
        if (string.IsNullOrEmpty(sanitizedPrefix))
            return new EmailProvisioningResult(false, ErrorMessage: "Email prefix contains characters that cannot be transliterated to ASCII. Please choose a different prefix.");

        var fullEmail = $"{sanitizedPrefix}@nobodies.team";

        try
        {
            // Check DB first: reject if the address is already tied to another human in our system.
            // Must run BEFORE the Workspace existence check so a stale/deleted Workspace account
            // cannot silently "move" the identity off its current human.
            //
            // Filter UserId != userId inside the query (not after FirstOrDefault) so duplicate
            // rows with mixed case or historical drift can't mask a real cross-user conflict.
            var conflictingEmailUserId = await _dbContext.UserEmails
                .Where(ue => string.Equals(ue.Email, fullEmail, StringComparison.OrdinalIgnoreCase)
                    && ue.UserId != userId)
                .Select(ue => (Guid?)ue.UserId)
                .FirstOrDefaultAsync();
            if (conflictingEmailUserId is not null)
            {
                _logger.LogWarning(
                    "Provisioning rejected: {Email} is already linked to user {ConflictUserId} (requested for {UserId})",
                    fullEmail, conflictingEmailUserId, userId);
                return new EmailProvisioningResult(false, fullEmail,
                    ErrorMessage: $"{fullEmail} is already in use by another human.");
            }

            var conflictingGoogleEmailUserId = await _dbContext.Users
                .Where(u => u.GoogleEmail != null
                    && string.Equals(u.GoogleEmail, fullEmail, StringComparison.OrdinalIgnoreCase)
                    && u.Id != userId)
                .Select(u => (Guid?)u.Id)
                .FirstOrDefaultAsync();
            if (conflictingGoogleEmailUserId is not null)
            {
                _logger.LogWarning(
                    "Provisioning rejected: {Email} is already set as GoogleEmail for user {ConflictUserId} (requested for {UserId})",
                    fullEmail, conflictingGoogleEmailUserId, userId);
                return new EmailProvisioningResult(false, fullEmail,
                    ErrorMessage: $"{fullEmail} is already in use by another human.");
            }

            // Reject if the prefix collides with a team's Google Group. Team groups
            // live on the same domain (@nobodies.team), so a user account with the
            // same address would cause mail-routing chaos and break group membership.
            var conflictingTeamName = await _dbContext.Teams
                .Where(t => t.GoogleGroupPrefix != null
                    && string.Equals(t.GoogleGroupPrefix, sanitizedPrefix, StringComparison.OrdinalIgnoreCase))
                .Select(t => t.Name)
                .FirstOrDefaultAsync();
            if (conflictingTeamName is not null)
            {
                _logger.LogWarning(
                    "Provisioning rejected: {Email} is the Google Group address for team '{TeamName}' (requested for {UserId})",
                    fullEmail, conflictingTeamName, userId);
                return new EmailProvisioningResult(false, fullEmail,
                    ErrorMessage: $"{fullEmail} is the Google Group address for team \"{conflictingTeamName}\" and cannot be used as a personal account.");
            }

            // Check if account already exists in Google Workspace
            var existing = await _workspaceUserService.GetAccountAsync(fullEmail);
            if (existing is not null)
                return new EmailProvisioningResult(false, fullEmail, ErrorMessage: $"Account {fullEmail} already exists in Google Workspace.");

            // Use real name from profile, not display/burner name
            var firstName = user.Profile?.FirstName;
            var lastName = user.Profile?.LastName;
            if (string.IsNullOrWhiteSpace(firstName) || string.IsNullOrWhiteSpace(lastName))
                return new EmailProvisioningResult(false, fullEmail, ErrorMessage: "Cannot provision account: the human must have a first and last name in their profile.");

            // ──────────────────────────────────────────────────────────────
            // ORDERING IS CRITICAL in this block.
            //
            // We must capture the user's personal (recovery) email BEFORE
            // calling AddVerifiedEmailAsync, because that call switches the
            // user's notification target to the new @nobodies.team address.
            // If we resolved the recovery email after that point, we'd send
            // the credentials email to the @nobodies.team mailbox — which the
            // user can't access yet (they don't have the password).
            //
            // Sequence:
            //   1. Capture recovery email  (personal address)
            //   2. Provision Google Workspace account
            //   3. Link @nobodies.team email  (changes notification target)
            //   4. Send credentials to recovery email captured in step 1
            //
            // Do NOT reorder these steps.
            // ──────────────────────────────────────────────────────────────

            // Step 1: Capture recovery email BEFORE the notification target changes.
            var recoveryEmail = user.GetEffectiveEmail();
            if (recoveryEmail?.EndsWith("@nobodies.team", StringComparison.OrdinalIgnoreCase) == true)
                recoveryEmail = user.Email; // fall back to OAuth email

            // Step 2: Generate temp password and provision in Google Workspace.
            var tempPassword = PasswordGenerator.GenerateTemporary();
            await _workspaceUserService.ProvisionAccountAsync(
                fullEmail, firstName, lastName, tempPassword,
                recoveryEmail);

            // Step 3: Link the new email — this changes the notification target
            // to @nobodies.team. Do NOT move this above step 1.
            await _userEmailService.AddVerifiedEmailAsync(userId, fullEmail);

            user.GoogleEmail = fullEmail;
            await _userManager.UpdateAsync(user);

            // Audit
            await _auditLogService.LogAsync(
                AuditAction.WorkspaceAccountProvisioned,
                "WorkspaceAccount", userId,
                $"Provisioned and linked @nobodies.team account: {fullEmail}",
                provisionedByUserId);
            await _dbContext.SaveChangesAsync();

            // Step 4: Send credentials to the PERSONAL email captured in step 1.
            if (!string.IsNullOrEmpty(recoveryEmail))
            {
                await _emailService.SendWorkspaceCredentialsAsync(
                    recoveryEmail, user.DisplayName, fullEmail, tempPassword,
                    user.PreferredLanguage);
            }

            // In-app notification (best-effort)
            try
            {
                await _notificationService.SendAsync(
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
                _logger.LogError(ex, "Failed to dispatch WorkspaceCredentialsReady notification for user {UserId}", userId);
            }

            return !string.IsNullOrEmpty(recoveryEmail)
                ? new EmailProvisioningResult(true, fullEmail, recoveryEmail)
                : new EmailProvisioningResult(true, fullEmail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to provision @nobodies.team account {Email} for user {UserId}", fullEmail, userId);
            return new EmailProvisioningResult(false, fullEmail, ErrorMessage: $"Failed to provision {fullEmail}. Check logs for details.");
        }
    }

    /// <summary>
    /// Transliterates an email prefix to ASCII by applying German-specific mappings
    /// (ü→ue, ö→oe, ä→ae, ß→ss) first, then stripping remaining diacritics via
    /// Unicode NFD decomposition. Returns null if the result contains non-ASCII or
    /// invalid email local-part characters; returns empty string if input was blank.
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
