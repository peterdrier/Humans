using Humans.Google.Contracts;
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Humans.AuditLog.Contracts;
using Humans.Users.Data.Repositories;
using Humans.Users.Contracts;
using Humans.Base.Helpers;
using Humans.Base.Threading;
using Humans.Users.Helpers;

namespace Humans.Users.Services;

internal sealed class UserEmailService(
    IUserRepository repository,
    IUserManagement userService,
    UserManager<User> userManager,
    IClock clock,
    IAuditLogService auditLogService,
    IServiceProvider serviceProvider,
    ILogger<UserEmailService> logger) : IUserEmailService, IUserMerge
{
    // Keep the last-sign-in check and removal together across request scopes.
    private static readonly ConcurrentDictionary<Guid, TrackedLock> RemovalLocks = new();

    private readonly UserEmailVerification verification = new(
        repository, userService, userManager, clock, auditLogService, serviceProvider, logger);
    private readonly UserEmailReader reader = new(repository, userService, clock, serviceProvider);
    private readonly UserEmailReconciliation reconciliation = new(repository, userService, clock, auditLogService, serviceProvider, logger);

    // Lazy: breaks DI cycle TicketQueryService -> IUserEmailService -> ITicketService.
    private Humans.Tickets.Contracts.ITicketService TicketService =>
        serviceProvider.GetRequiredService<Humans.Tickets.Contracts.ITicketService>();

    // Lazy: Google's identity service reads IUserService.
    private IGoogleWrite GoogleIdentity => serviceProvider.GetRequiredService<IGoogleWrite>();

    public Task<IReadOnlyList<UserEmailEditDto>> GetUserEmailsAsync(
        Guid userId, CancellationToken cancellationToken = default) =>
        reader.GetUserEmailsAsync(userId, cancellationToken);

    public Task<(bool CanAdd, int MinutesUntilResend, Guid? PendingEmailId)> GetEmailCooldownInfoAsync(
        Guid pendingEmailId, CancellationToken ct = default) =>
        reader.GetEmailCooldownInfoAsync(pendingEmailId, ct);

    public Task<IReadOnlyList<UserEmailDto>> GetVisibleEmailsAsync(
        Guid userId, ContactFieldVisibility accessLevel, CancellationToken cancellationToken = default) =>
        reader.GetVisibleEmailsAsync(userId, accessLevel, cancellationToken);

    public Task<AddEmailResult> AddEmailAsync(
        Guid userId, string email, CancellationToken cancellationToken = default, Guid? actorUserId = null) =>
        verification.AddEmailAsync(userId, email, cancellationToken, actorUserId);

    public Task<VerifyEmailResult> VerifyEmailAsync(
        Guid userId, Guid emailId, string token, CancellationToken cancellationToken = default) =>
        verification.VerifyEmailAsync(userId, emailId, token, cancellationToken);

    public Task<VerifyEmailResult> AdminMarkVerifiedAsync(
        Guid userId, Guid emailId, Guid actorUserId, CancellationToken cancellationToken = default) =>
        verification.AdminMarkVerifiedAsync(userId, emailId, actorUserId, cancellationToken);

    public async Task<UpdateEmailResult> SetPrimaryAsync(
        Guid userId, Guid emailId, CancellationToken cancellationToken = default, Guid? actorUserId = null)
    {
        var result = await userService.UpdateUserEmailAsync(
            userId,
            emailId,
            new UserEmailUpdateCommand(Primary: UserEmailPrimaryChange.MakePrimary),
            cancellationToken);
        if (result.Succeeded)
            await LogEmailChangeAsync(UsersAuditActions.UserEmailPrimarySet, userId, emailId,
                $"Set primary email row {emailId}", actorUserId);
        return result;
    }

    public async Task<UpdateEmailResult> SetVisibilityAsync(
        Guid userId, Guid emailId, ContactFieldVisibility? visibility,
        CancellationToken cancellationToken = default, Guid? actorUserId = null)
    {
        var result = await userService.UpdateUserEmailAsync(
            userId,
            emailId,
            new UserEmailUpdateCommand(ChangeVisibility: true, Visibility: visibility),
            cancellationToken);
        if (result.Succeeded)
            await LogEmailChangeAsync(UsersAuditActions.UserEmailVisibilityChanged, userId, emailId,
                $"Changed visibility on email row {emailId} to {(visibility?.ToString() ?? "hidden")}", actorUserId);
        return result;
    }

    public async Task<DeleteEmailResult> DeleteEmailAsync(
        Guid userId, Guid emailId, CancellationToken cancellationToken = default, Guid? actorUserId = null)
    {
        using var removal = await RemovalLocks.GetOrAdd(userId,
            static id => new TrackedLock($"Users.EmailRemoval[{id}]")).AcquireAsync(logger, cancellationToken);
        var email = await repository.GetUserEmailByIdAndUserIdAsync(emailId, userId, cancellationToken);
        if (email is null)
            return new DeleteEmailResult(false, "Users_EmailUpdate_NotFound");

        if (!string.IsNullOrEmpty(email.Provider))
        {
            // Provider-attached rows must go through UnlinkAsync; this guards non-UI callers.
            return new DeleteEmailResult(false, "Users_EmailGrid_DeleteRejectedHasProvider");
        }

        // Block removing the primary: the user must promote another verified email first, or
        // they can delete their own sign-in/notification target.
        if (email.IsPrimary)
        {
            return new DeleteEmailResult(false, "Users_EmailGrid_DeletePrimaryBlocked");
        }

        // Block deleting the last verified row: the user would silently lose all notifications.
        if (email.IsVerified)
        {
            var allEmails = await repository.GetUserEmailsByUserIdForMutationAsync(userId, cancellationToken);
            var verifiedRemaining = allEmails.Count(e => e.IsVerified && e.Id != emailId);

            if (verifiedRemaining == 0)
            {
                return new DeleteEmailResult(false, "Users_EmailDelete_LastVerifiedBlocked");
            }
        }

        // Block removing a ticket-linked email: the next vendor re-sync could un-match the ticket.
        if (await IsAddressTicketLinkedAsync(userId, email.Email, cancellationToken))
        {
            return new DeleteEmailResult(false, "Users_EmailGrid_DeleteTicketLinkedBlocked");
        }

        var removed = await userService.RemoveUserEmailAsync(
            userId,
            emailId,
            new UserEmailRemoveCommand(UserEmailRemovalMode.PlainEmail),
            cancellationToken);
        if (removed)
        {
            await GoogleIdentity.ClearIfAddressAsync(userId, email.Email, CancellationToken.None);
            // Without an actor the caller is transactional provisioning, which audits after its commit.
            if (actorUserId.HasValue)
                await LogEmailChangeAsync(UsersAuditActions.UserEmailDeleted, userId, emailId,
                    $"Deleted email row {emailId}", actorUserId);
        }
        return new DeleteEmailResult(removed);
    }

    /// <inheritdoc />
    public async Task ReassignAsync(Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now,
        CancellationToken ct)
    {
        // Caller invalidates the cache after this completes.
        await repository.ReassignUserEmailsToUserAsync(
            mergedFromUserId, mergedToUserId, now, ct);
    }

    public async Task<UserEmailAddResult> AddVerifiedEmailAsync(
        Guid userId, string email, CancellationToken cancellationToken = default, Guid? actorUserId = null)
    {
        var result = await userService.AddUserEmailAsync(
            userId,
            new UserEmailAddCommand(
                email,
                IsVerified: true,
                Visibility: ContactFieldVisibility.BoardOnly,
                IgnoreExisting: true),
            cancellationToken);

        if (result.Added && result.ErrorKey is null && actorUserId.HasValue)
            await LogEmailChangeAsync(UsersAuditActions.UserEmailAdded, userId, result.EmailId,
                $"Added pre-verified email {email.Trim()} (no verification flow)", actorUserId);
        return result;
    }

    public Task<IReadOnlyDictionary<Guid, string>> GetNotificationTargetEmailsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default) =>
        reader.GetNotificationTargetEmailsAsync(userIds, cancellationToken);

    public Task<IReadOnlyList<UserEmailRowSnapshot>> FindByAddressAsync(
        string email, bool aliased, bool verifiedOnly, CancellationToken cancellationToken = default) =>
        reader.FindByAddressAsync(email, aliased, verifiedOnly, cancellationToken);

    public Task<IReadOnlyList<Guid>> GetUserIdsByEmailPrefixAndSuffixAsync(
        string prefix, string suffix, CancellationToken cancellationToken = default) =>
        reader.GetUserIdsByEmailPrefixAndSuffixAsync(prefix, suffix, cancellationToken);

    public Task<IReadOnlyList<UserEmailRowSnapshot>> GetEntitiesByUserIdAsync(
        Guid userId, CancellationToken cancellationToken = default) =>
        reader.GetEntitiesByUserIdAsync(userId, cancellationToken);

    public Task<IReadOnlyDictionary<Guid, string>> GetNotificationEmailsByUserIdsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default) =>
        reader.GetNotificationEmailsByUserIdsAsync(userIds, cancellationToken);

    public Task<IReadOnlyList<UserEmailMatch>> MatchByEmailsAsync(
        IReadOnlyCollection<string> emails, CancellationToken cancellationToken = default) =>
        reader.MatchByEmailsAsync(emails, cancellationToken);

    // Order buyer or matched attendee, read through the Tickets section's own interface.
    private async Task<bool> IsAddressTicketLinkedAsync(
        Guid userId, string address, CancellationToken cancellationToken)
    {
        var orders = await TicketService.GetTicketOrdersAsync(cancellationToken);

        foreach (var order in orders)
        {
            if (order.MatchedUserId == userId
                && EmailNormalization.EmailsMatch(address, order.BuyerEmail))
                return true;

            if (order.Attendees.Any(a => a.MatchedUserId == userId
                    && EmailNormalization.EmailsMatch(address, a.AttendeeEmail)))
                return true;
        }

        return false;
    }
    /// <inheritdoc />
    public async Task AddProvisionedEmailAsync(
        Guid userId, string email, CancellationToken cancellationToken = default)
    {
        // Idempotent: retried imports must not re-add the row.
        var result = await userService.AddUserEmailAsync(
            userId,
            new UserEmailAddCommand(
                email,
                IsVerified: true,
                IgnoreExisting: true),
            cancellationToken);
        if (result.ErrorKey is not null)
            throw new InvalidOperationException("Could not add the provisioned email address.");
    }

    /// <inheritdoc />
    public async Task<bool> ClearPrimaryAsync(
        Guid userId, Guid userEmailId, Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var row = await repository.GetUserEmailByIdAndUserIdAsync(userEmailId, userId, cancellationToken);
        if (row is null || !row.IsPrimary) return false;

        // Duplicate verified rows only, mirroring the scanner's filter, so this can't cause ZeroIsPrimary.
        var allEmails = await repository.GetUserEmailsByUserIdReadOnlyAsync(userId, cancellationToken);
        if (allEmails.Count(e => e.IsPrimary && e.IsVerified) <= 1) return false;

        var updated = await userService.UpdateUserEmailAsync(
            userId,
            row.Id,
            new UserEmailUpdateCommand(Primary: UserEmailPrimaryChange.ClearDuplicatePrimary),
            cancellationToken);
        if (!updated.Succeeded) return false;
        // No auto-promote: the admin picks the new primary deliberately.
        await auditLogService.LogAsync(
            UsersAuditActions.UserEmailPrimaryCleared,
            nameof(User), userId,
            $"Cleared primary flag from {row.Email}",
            actorUserId,
            relatedEntityId: row.Id, relatedEntityType: nameof(UserEmail));

        return true;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<UserEmailFlagViolation>> GetEmailFlagViolationsAsync(
        CancellationToken cancellationToken = default) =>
        reader.GetEmailFlagViolationsAsync(cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<UserEmailOrphan>> GetOrphanUserEmailsAsync(CancellationToken ct = default) =>
        reader.GetOrphanUserEmailsAsync(ct);

    /// <inheritdoc />
    public async Task<bool> DeleteByIdAsync(Guid emailId, CancellationToken ct = default, Guid? actorUserId = null)
    {
        var row = await repository.GetUserEmailByIdReadOnlyAsync(emailId, ct);
        if (row is null) return false;

        var removed = await userService.RemoveUserEmailAsync(
            row.UserId,
            emailId,
            new UserEmailRemoveCommand(
                UserEmailRemovalMode.AnyEmail,
                PreserveLastVerifiedEmail: false),
            ct);
        if (removed)
        {
            await GoogleIdentity.ClearIfAddressAsync(row.UserId, row.Email, CancellationToken.None);
            const string description = "Orphan UserEmail row deleted by EmailProblems action";
            if (actorUserId is { } actor)
                await auditLogService.LogAsync(UsersAuditActions.OrphanUserEmailDeleted,
                    nameof(UserEmail), emailId, description, actor);
            else
                await auditLogService.LogAsync(UsersAuditActions.OrphanUserEmailDeleted,
                    nameof(UserEmail), emailId, description, nameof(UserEmailService));
        }
        return removed;
    }

    /// <inheritdoc />
    public async Task<UnlinkEmailResult> UnlinkAsync(
        Guid userId, Guid? userEmailId, Guid actorUserId,
        CancellationToken cancellationToken = default,
        string? provider = null, string? providerKey = null)
    {
        using var removal = await RemovalLocks.GetOrAdd(userId,
            static id => new TrackedLock($"Users.EmailRemoval[{id}]")).AcquireAsync(logger, cancellationToken);
        var allEmails = await repository.GetUserEmailsByUserIdForMutationAsync(userId, cancellationToken);
        UserEmail? row;
        if (userEmailId.HasValue)
        {
            row = await repository.GetUserEmailByIdAndUserIdAsync(userEmailId.Value, userId, cancellationToken);
            if (row is null) return new(false);
            provider = row.Provider;
            providerKey = row.ProviderKey;
        }
        else
        {
            row = allEmails.FirstOrDefault(e =>
                string.Equals(e.Provider, provider, StringComparison.Ordinal)
                && string.Equals(e.ProviderKey, providerKey, StringComparison.Ordinal));
        }
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(providerKey))
            return new(false);

        // An orphan or unverified row can still carry the only OAuth login; magic link needs a verified email.
        if (!CanUnlink(allEmails.Count(e => e.IsVerified), row?.IsVerified == true))
        {
            logger.LogWarning(
                "Refused unlink of email {EmailId} for user {UserId}: no other verified sign-in method remains",
                row?.Id, userId);
            return new(false, "Users_LinkedAccounts_UnlinkBlockedLastSignInMethod");
        }

        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null) return new(false);
        if (!userEmailId.HasValue)
        {
            var logins = await userManager.GetLoginsAsync(user);
            if (!logins.Any(l => string.Equals(l.LoginProvider, provider, StringComparison.Ordinal)
                && string.Equals(l.ProviderKey, providerKey, StringComparison.Ordinal)))
                return new(false);
        }

        var removeLogin = await userManager.RemoveLoginAsync(user, provider, providerKey);
        if (!removeLogin.Succeeded)
        {
            logger.LogError(
                "UnlinkAsync: UserManager.RemoveLoginAsync failed for user {UserId} provider {Provider}; aborting unlink to preserve consistency between AspNetUserLogins and user_emails. Errors: {Errors}",
                userId, provider,
                string.Join("; ", removeLogin.Errors.Select(e => $"{e.Code}:{e.Description}")));
            return new(false);
        }

        // Identity removal committed; finish row, invariants, cache and audit even after a disconnect.
        cancellationToken = CancellationToken.None;
        if (row is not null)
        {
            var removed = await userService.RemoveUserEmailAsync(
                userId,
                row.Id,
                new UserEmailRemoveCommand(
                    UserEmailRemovalMode.ProviderLinkedEmail,
                    PreserveLastVerifiedEmail: false),
                cancellationToken);
            if (!removed) return new(false);
            await GoogleIdentity.ClearIfAddressAsync(userId, row.Email, cancellationToken);
        }

        await auditLogService.LogAsync(
            UsersAuditActions.UserEmailUnlinked,
            nameof(User), userId,
            row is null
                ? $"Unlinked orphan {provider} login (no matching UserEmail row)"
                : $"Unlinked {provider} (key {ShortProviderKeyHash(providerKey)}) from {row.Email}",
            actorUserId,
            relatedEntityId: row?.Id, relatedEntityType: row is null ? null : nameof(UserEmail));

        return new(true);
    }

    private Task LogEmailChangeAsync(AuditActionDefinition action, Guid userId, Guid emailId,
        string description, Guid? actorUserId) =>
        actorUserId is { } actor
            ? auditLogService.LogAsync(action, nameof(User), userId, description, actor,
                relatedEntityId: emailId, relatedEntityType: nameof(UserEmail))
            : auditLogService.LogAsync(action, nameof(User), userId, description, nameof(UserEmailService),
                relatedEntityId: emailId, relatedEntityType: nameof(UserEmail));

    internal static bool CanUnlink(int verifiedEmailCount, bool removesVerifiedEmail) =>
        verifiedEmailCount - (removesVerifiedEmail ? 1 : 0) >= 1;

    internal static string ShortProviderKeyHash(string providerKey)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(providerKey));
        return Convert.ToHexString(bytes.AsSpan(0, 8));
    }

    /// <inheritdoc />
    public Task<OAuthReconcileResult> ReconcileOAuthIdentityAsync(
        Guid userId,
        string provider,
        string providerKey,
        string claimEmail,
        bool claimEmailVerified,
        CancellationToken cancellationToken = default) =>
        reconciliation.ReconcileAsync(userId, provider, providerKey, claimEmail, claimEmailVerified, cancellationToken);

}
