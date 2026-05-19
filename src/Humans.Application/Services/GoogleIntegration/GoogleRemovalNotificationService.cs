using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Humans.Application.Services.GoogleIntegration;

/// <summary>
/// Decides Variant 1 (loss of access) vs Variant 2 (secondary cleanup) and sends as MessageCategory.System.
/// Suppresses orphan addresses (#639). EmailRotation is plumbed for telemetry but doesn't suppress.
/// </summary>
public sealed class GoogleRemovalNotificationService(
    IUserEmailService userEmailService,
    IUserService userService,
    IEmailService emailService,
    ILogger<GoogleRemovalNotificationService> logger) : IGoogleRemovalNotificationService
{
    /// <inheritdoc />
    public async Task NotifyRemovalAsync(
        string removedEmail,
        GoogleResourceType resourceType,
        string? resourceName,
        string? resourceIdentifier,
        SyncRemovalReason reason,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(removedEmail))
        {
            return;
        }

        // Resolve recipient from the removed address. Orphan = no UserEmail
        // row; this captures user-deleted/anonymized and self-unlink cases
        // (the UserEmail row is gone before reconciliation runs).
        var userId = await userEmailService.GetUserIdByVerifiedEmailAsync(removedEmail, cancellationToken);
        if (userId is null)
        {
            // Expected condition (deleted user, anonymized human, self-unlink,
            // OAuth-rename-in-place) but visible-in-prod-log per
            // memory/code/always-log-problems.md — incident investigation
            // needs to see suppressed notifications.
            logger.LogWarning(
                "Suppressing Google removal notification for {Email} — no UserEmail row matches " +
                "(orphan, deleted user, or self-unlink)", removedEmail);
            return;
        }

        // Load the user with all UserEmails so we can run the variant selector.
        var usersById = await userService.GetUserInfosAsync([userId.Value], cancellationToken);
        if (!usersById.TryGetValue(userId.Value, out var user))
        {
            logger.LogWarning(
                "Google removal notification: UserEmail mapped {Email} to user {UserId} but " +
                "the user could not be loaded — skipping", removedEmail, userId.Value);
            return;
        }

        var userName = !string.IsNullOrWhiteSpace(user.BurnerName)
            ? user.BurnerName
            : removedEmail;
        var culture = string.IsNullOrWhiteSpace(user.PreferredLanguage) ? "en" : user.PreferredLanguage;

        // Variant selector: does the user retain another verified IsGoogle
        // address that is NOT the one being removed? If yes → secondary
        // cleanup (Variant 2). Otherwise → loss of access (Variant 1).
        var otherGoogleEmail = FindOtherVerifiedGoogleEmail(user, removedEmail);

        if (otherGoogleEmail is not null)
        {
            await emailService.SendGoogleAccessRemovalSecondaryCleanupAsync(
                removedEmail,
                userName,
                otherGoogleEmail,
                culture,
                cancellationToken);
            logger.LogInformation(
                "Sent Google removal Variant 2 (secondary cleanup) to {Email} for user {UserId}; " +
                "primary remains {Primary}",
                removedEmail, user.Id, otherGoogleEmail);
            return;
        }

        // Variant 1 — sub-template by resource type. Drive variants
        // (DriveFolder, SharedDrive, DriveFile) all map to the Drive
        // sub-template since the message is "your access to {resource} has
        // been removed".
        var displayName = !string.IsNullOrWhiteSpace(resourceName)
            ? resourceName
            : (!string.IsNullOrWhiteSpace(resourceIdentifier) ? resourceIdentifier : "(unknown)");

        if (resourceType == GoogleResourceType.Group)
        {
            // Spec: surface the group email in subject + body. Fall back to
            // the resource name when the identifier is missing.
            var groupEmail = !string.IsNullOrWhiteSpace(resourceIdentifier)
                ? resourceIdentifier
                : displayName;
            await emailService.SendGoogleGroupRemovalLossOfAccessAsync(
                removedEmail,
                userName,
                displayName,
                groupEmail,
                culture,
                cancellationToken);
            logger.LogInformation(
                "Sent Google removal Variant 1 (group loss-of-access) to {Email} for user {UserId} group {Group}",
                removedEmail, user.Id, displayName);
        }
        else
        {
            await emailService.SendGoogleDriveRemovalLossOfAccessAsync(
                removedEmail,
                userName,
                displayName,
                culture,
                cancellationToken);
            logger.LogInformation(
                "Sent Google removal Variant 1 (drive loss-of-access) to {Email} for user {UserId} folder {Folder}",
                removedEmail, user.Id, displayName);
        }
    }

    /// <summary>
    /// Returns a verified <c>IsGoogle</c> email belonging to the user that
    /// is NOT <paramref name="removedEmail"/>, or <c>null</c> when no such
    /// row exists. Drives the Variant 1 / Variant 2 split.
    /// </summary>
    private static string? FindOtherVerifiedGoogleEmail(UserInfo user, string removedEmail)
    {
        foreach (var ue in user.UserEmails)
        {
            if (!ue.IsVerified || !ue.IsGoogle)
            {
                continue;
            }
            if (string.Equals(ue.Email, removedEmail, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            return ue.Email;
        }

        return null;
    }
}
