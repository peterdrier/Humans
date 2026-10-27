using Humans.Base.Extensions;
using Humans.GoogleIntegration.Contracts;
using Humans.Email.Contracts;
using Humans.Users.Contracts;

namespace Humans.GoogleIntegration.Services;

/// <summary>
/// Picks Variant 1 (loss of access) or Variant 2 (secondary cleanup). Orphan addresses are suppressed;
/// EmailRotation is telemetry only and does not suppress.
/// </summary>
internal sealed class GoogleRemovalNotificationService(
    IUserEmailService userEmailService,
    IUserService userService,
    IGoogleIntegrationService workspaceIdentities,
    IEmailService emailService,
    GoogleIntegrationEmails emailMessages,
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

        // Orphan = no UserEmail row: the member was deleted or anonymized, or unlinked it before reconciliation ran.
        var userId = (await userEmailService.FindByAddressAsync(
                removedEmail, aliased: false, verifiedOnly: true, cancellationToken))
            .FirstOrDefault()?.UserId;
        if (userId is null)
        {
            // Expected, but logged so an incident investigation can see suppressed notifications.
            logger.LogWarning(
                "Suppressing Google removal notification for {Email} — no UserEmail row matches " +
                "(orphan, deleted user, or self-unlink)", removedEmail);
            return;
        }

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
        var culture = user.PreferredLanguage.IsSupportedCultureCode()
            ? user.PreferredLanguage
            : CultureCatalog.DefaultCultureCode;

        var identity = await workspaceIdentities.GetWorkspaceIdentityAsync(user.Id, cancellationToken);
        var otherGoogleEmail = identity is not null
            && !string.Equals(identity.Email, removedEmail, StringComparison.OrdinalIgnoreCase)
                ? identity.Email
                : null;

        if (otherGoogleEmail is not null)
        {
            await emailService.SendAsync(emailMessages.GoogleAccessRemovalSecondaryCleanup(
                user.Id,
                removedEmail,
                userName,
                otherGoogleEmail,
                culture),
                cancellationToken);
            logger.LogInformation(
                "Sent Google removal Variant 2 (secondary cleanup) to {Email} for user {UserId}; " +
                "primary remains {Primary}",
                removedEmail, user.Id, otherGoogleEmail);
            return;
        }

        // Variant 1. All Drive types share the Drive sub-template.
        var displayName = !string.IsNullOrWhiteSpace(resourceName)
            ? resourceName
            : (!string.IsNullOrWhiteSpace(resourceIdentifier) ? resourceIdentifier : "(unknown)");

        if (resourceType == GoogleResourceType.Group)
        {
            // Spec: the group email goes in subject and body; the resource name is the fallback.
            var groupEmail = !string.IsNullOrWhiteSpace(resourceIdentifier)
                ? resourceIdentifier
                : displayName;
            await emailService.SendAsync(emailMessages.GoogleGroupRemovalLossOfAccess(
                user.Id,
                removedEmail,
                userName,
                displayName,
                groupEmail,
                culture),
                cancellationToken);
            logger.LogInformation(
                "Sent Google removal Variant 1 (group loss-of-access) to {Email} for user {UserId} group {Group}",
                removedEmail, user.Id, displayName);
        }
        else
        {
            await emailService.SendAsync(emailMessages.GoogleDriveRemovalLossOfAccess(
                user.Id,
                removedEmail,
                userName,
                displayName,
                culture),
                cancellationToken);
            logger.LogInformation(
                "Sent Google removal Variant 1 (drive loss-of-access) to {Email} for user {UserId} folder {Folder}",
                removedEmail, user.Id, displayName);
        }
    }
}
