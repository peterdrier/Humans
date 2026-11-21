using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using NodaTime;
using Humans.AuditLog.Contracts;
using Humans.Users.Data.Repositories;
using Humans.Users.Contracts;
using Humans.Base.Helpers;
using Humans.Base.Threading;
using Humans.Users.Helpers;
using Humans.Base.Interfaces;
using System.Web;
using Humans.Base.Configuration;
using Humans.Base.Extensions;
using Humans.Email.Contracts;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Humans.Users.Services;

internal sealed class UserEmailService(
    IUserRepository repository,
    IUserManagement userService,
    UserManager<User> userManager,
    IClock clock,
    IAuditLogService auditLogService,
    IAccountMergeRepository mergeRepository,
    IEnumerable<IUserEmailLock> emailLocks,
    IEnumerable<IUserEmailRemovalListener> removalListeners,
    UsersEmails usersEmails,
    IOptions<EmailSettings> emailSettings,
    IEmailService emailService,
    ILogger<UserEmailService> logger) : IUserEmailService, IUserMerge
{
    // Keep the last-sign-in check and removal together across request scopes.
    private static readonly ConcurrentDictionary<Guid, TrackedLock> RemovalLocks = new();

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

        if (await IsAddressLockedAsync(userId, email.Email, cancellationToken))
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
            await AddressRemovedAsync(userId, email.Email, CancellationToken.None);
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

    private async Task<bool> IsAddressLockedAsync(
        Guid userId, string address, CancellationToken cancellationToken)
    {
        foreach (var emailLock in emailLocks)
        {
            if ((await emailLock.GetLockedAddressesAsync(userId, cancellationToken))
                .Any(locked => EmailNormalization.EmailsMatch(address, locked)))
                return true;
        }

        return false;
    }

    private async Task AddressRemovedAsync(Guid userId, string email, CancellationToken cancellationToken)
    {
        foreach (var listener in removalListeners)
            await listener.AddressRemovedAsync(userId, email, cancellationToken);
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
            AuditSubjectTypes.User, userId,
            $"Cleared primary flag from {row.Email}",
            actorUserId,
            relatedEntityId: row.Id, relatedEntityType: AuditSubjectTypes.UserEmail);

        return true;
    }

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
            await AddressRemovedAsync(row.UserId, row.Email, CancellationToken.None);
            const string description = "Orphan UserEmail row deleted by EmailProblems action";
            if (actorUserId is { } actor)
                await auditLogService.LogAsync(UsersAuditActions.OrphanUserEmailDeleted,
                    AuditSubjectTypes.UserEmail, emailId, description, actor);
            else
                await auditLogService.LogAsync(UsersAuditActions.OrphanUserEmailDeleted,
                    AuditSubjectTypes.UserEmail, emailId, description, nameof(UserEmailService));
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
            await AddressRemovedAsync(userId, row.Email, cancellationToken);
        }

        await auditLogService.LogAsync(
            UsersAuditActions.UserEmailUnlinked,
            AuditSubjectTypes.User, userId,
            row is null
                ? $"Unlinked orphan {provider} login (no matching UserEmail row)"
                : $"Unlinked {provider} (key {ShortProviderKeyHash(providerKey)}) from {row.Email}",
            actorUserId,
            relatedEntityId: row?.Id, relatedEntityType: row is null ? null : AuditSubjectTypes.UserEmail);

        return new(true);
    }

    private Task LogEmailChangeAsync(AuditActionDefinition action, Guid userId, Guid emailId,
        string description, Guid? actorUserId) =>
        actorUserId is { } actor
            ? auditLogService.LogAsync(action, AuditSubjectTypes.User, userId, description, actor,
                relatedEntityId: emailId, relatedEntityType: AuditSubjectTypes.UserEmail)
            : auditLogService.LogAsync(action, AuditSubjectTypes.User, userId, description, nameof(UserEmailService),
                relatedEntityId: emailId, relatedEntityType: AuditSubjectTypes.UserEmail);

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
        ReconcileAsync(userId, provider, providerKey, claimEmail, claimEmailVerified, cancellationToken);

    public async Task<IReadOnlyList<UserEmailEditDto>> GetUserEmailsAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var info = await userService.GetUserInfoAsync(userId, cancellationToken);
        if (info is null) return [];

        var emailIds = info.UserEmails.Select(e => e.Id).ToList();
        var mergePendingSet = await mergeRepository.GetPendingEmailIdsAsync(emailIds, cancellationToken);
        var verifiedCount = info.UserEmails.Count(e => e.IsVerified);

        return info.UserEmails.Select(e => new UserEmailEditDto(
            e.Id,
            e.Email,
            e.IsVerified,
            e.Provider,
            e.ProviderKey,
            e.IsPrimary,
            e.Visibility,
            IsPendingVerification: !e.IsVerified && e.VerificationSentAt.HasValue,
            IsMergePending: mergePendingSet.Contains(e.Id),
            CanUnlink: !string.IsNullOrEmpty(e.Provider) && !string.IsNullOrEmpty(e.ProviderKey)
                && UserEmailService.CanUnlink(verifiedCount, e.IsVerified)
        )).ToList();
    }

    public async Task<(bool CanAdd, int MinutesUntilResend, Guid? PendingEmailId)>
        GetEmailCooldownInfoAsync(Guid pendingEmailId, CancellationToken ct = default)
    {
        var pendingRecord = await repository.GetUserEmailByIdReadOnlyAsync(pendingEmailId, ct);

        if (pendingRecord?.VerificationSentAt.HasValue == true)
        {
            var now = clock.GetCurrentInstant();
            var cooldownEnd = pendingRecord.VerificationSentAt.Value.Plus(Duration.FromMinutes(5));
            if (now < cooldownEnd)
            {
                var minutesUntilResend = (int)Math.Ceiling((cooldownEnd - now).TotalMinutes);
                return (false, minutesUntilResend, pendingEmailId);
            }
        }

        return (true, 0, null);
    }

    public async Task<IReadOnlyList<UserEmailDto>> GetVisibleEmailsAsync(
        Guid userId, ContactFieldVisibility accessLevel,
        CancellationToken cancellationToken = default)
    {
        var info = await userService.GetUserInfoAsync(userId, cancellationToken);
        if (info is null) return [];

        var allowed = ContactFieldVisibilityRules.GetAllowedVisibilities(accessLevel);

        return info.UserEmails
            .Where(e => e.IsVerified && e.Visibility != null && allowed.Contains(e.Visibility!.Value))
            .Select(e => new UserEmailDto(
                e.Id,
                e.Email,
                e.IsVerified,
                e.Provider,
                e.ProviderKey,
                e.IsPrimary,
                e.Visibility))
            .ToList();
    }

    public async Task<IReadOnlyDictionary<Guid, string>> GetNotificationTargetEmailsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default)
    {
        if (userIds.Count == 0)
            return new Dictionary<Guid, string>();

        // Resolve first: a tombstone's own address is a sentinel that bounces, and its
        // notification rows moved to the survivor. The key stays the requested id.
        var resolved = await userService.GetUserInfosAsync(userIds, cancellationToken);

        var result = new Dictionary<Guid, string>(userIds.Count);
        foreach (var userId in userIds)
        {
            if (!resolved.TryGetValue(userId, out var user))
                continue;

            if (!string.IsNullOrWhiteSpace(user.Email))
                result[userId] = user.Email;
        }

        return result;
    }

    public async Task<IReadOnlyList<UserEmailRowSnapshot>> FindByAddressAsync(
        string email, bool aliased, bool verifiedOnly, CancellationToken cancellationToken = default)
    {
        // EmailsMatch folds gmail/googlemail on both sides, so the aliased form needs no
        // separate alternate-address pass.
        email = email.Trim();
        var infos = await userService.GetAllUserInfosAsync(cancellationToken);
        return infos
            .SelectMany(i => i.UserEmails
                .Where(e => (!verifiedOnly || e.IsVerified) &&
                            (aliased
                                ? EmailNormalization.EmailsMatch(e.Email, email)
                                : string.Equals(e.Email, email, StringComparison.OrdinalIgnoreCase)))
                .Select(e => ToSnapshot(i.Id, e)))
            .ToList();
    }

    public Task<IReadOnlyList<Guid>> GetUserIdsByEmailPrefixAndSuffixAsync(
        string prefix,
        string suffix,
        CancellationToken cancellationToken = default) =>
        repository.GetUserIdsByUserEmailPrefixAndSuffixAsync(prefix, suffix, cancellationToken);

    public async Task<IReadOnlyList<UserEmailRowSnapshot>> GetEntitiesByUserIdAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        // Raw: callers edit and delete rows by the owner id stamped on them, so a resolving read
        // would hand a tombstone's caller the survivor's rows stamped with the archived id.
        var info = await userService.GetRawUserInfoAsync(userId, cancellationToken);
        if (info is null) return [];
        return info.UserEmails.Select(e => ToSnapshot(userId, e)).ToList();
    }

    private static UserEmailRowSnapshot ToSnapshot(Guid userId, UserEmailInfo email) =>
        new(
            email.Id,
            userId,
            email.Email,
            email.IsVerified,
            email.Provider,
            email.ProviderKey,
            email.IsPrimary,
            email.Visibility,
            email.VerificationSentAt,
            email.CreatedAt,
            email.UpdatedAt);

    public async Task<IReadOnlyDictionary<Guid, string>> GetNotificationEmailsByUserIdsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default)
    {
        if (userIds.Count == 0)
            return new Dictionary<Guid, string>();

        var requestedIds = userIds.ToHashSet();
        var users = await userService.GetAllRawUserInfosAsync(cancellationToken);
        return users
            .Where(user => requestedIds.Contains(user.Id) && user.PrimaryEmail != null)
            .ToDictionary(user => user.Id, user => user.PrimaryEmail!);
    }

    public async Task<IReadOnlyList<UserEmailMatch>> MatchByEmailsAsync(
        IReadOnlyCollection<string> emails, CancellationToken cancellationToken = default)
    {
        var rows = await repository.GetUserEmailsByEmailsAsync(emails, cancellationToken);
        return rows
            .Select(r => new UserEmailMatch(
                r.Email, r.UserId, r.IsPrimary, r.IsVerified, r.UpdatedAt))
            .ToList();
    }
    public async Task<IReadOnlyList<UserEmailFlagViolation>> GetEmailFlagViolationsAsync(
        CancellationToken cancellationToken = default)
    {
        var infos = await userService.GetAllUserInfosAsync(cancellationToken);

        var violations = new List<UserEmailFlagViolation>();
        foreach (var info in infos)
        {
            if (info.UserEmails.Count == 0) continue;

            var verified = info.UserEmails.Where(e => e.IsVerified).ToList();
            var verifiedPrimaryCount = verified.Count(e => e.IsPrimary);
            var hasPrimaryProblem = verified.Count > 0 && verifiedPrimaryCount != 1;

            if (!hasPrimaryProblem) continue;

            violations.Add(new UserEmailFlagViolation(
                info.Id,
                verified.Count,
                verifiedPrimaryCount,
                hasPrimaryProblem));
        }

        return violations;
    }

    public async Task<IReadOnlyList<UserEmailOrphan>> GetOrphanUserEmailsAsync(CancellationToken ct = default)
    {
        // Iterating UserInfo can't find rows for missing users, so this needs the full-table scan.
        var allEmails = await repository.GetAllUserEmailsAsync(ct);
        var liveUserIds = (await userService.GetAllUserInfosAsync(ct).ConfigureAwait(false))
            .Select(u => u.Id)
            .ToHashSet();

        return allEmails
            .Where(e => !liveUserIds.Contains(e.UserId))
            .Select(e => new UserEmailOrphan(e.UserId, e.Id, e.Email))
            .ToList();
    }

    // Lazy: Google's identity service reads IUserService.

    public async Task<OAuthReconcileResult> ReconcileAsync(
        Guid userId,
        string provider,
        string providerKey,
        string claimEmail,
        bool claimEmailVerified,
        CancellationToken cancellationToken = default)
    {
        var now = clock.GetCurrentInstant();
        var rows = (await repository.GetUserEmailsByUserIdForMutationAsync(userId, cancellationToken)).ToList();

        var tagged = rows.FirstOrDefault(r =>
            string.Equals(r.Provider, provider, StringComparison.Ordinal) &&
            string.Equals(r.ProviderKey, providerKey, StringComparison.Ordinal));

        // 1. NoChange
        if (tagged is not null &&
            string.Equals(tagged.Email, claimEmail, StringComparison.OrdinalIgnoreCase))
        {
            return new OAuthReconcileResult(
                ReconcileOutcome.NoChange, null, tagged.Id, null, null, null, false);
        }

        var siblingAtClaim = rows.FirstOrDefault(r =>
            !ReferenceEquals(r, tagged) &&
            string.Equals(r.Email, claimEmail, StringComparison.OrdinalIgnoreCase));

        // Cross-user check before any mutation, normalized so dot-aliases can't bypass the gate.
        var normalizedClaim = EmailNormalization.NormalizeForComparison(claimEmail);
        var alternateClaim = EmailLookupAliases.GetAlternate(normalizedClaim);
        var blocker = (await repository.GetUserEmailsByAddressAsync(
                normalizedClaim, alternateClaim, cancellationToken))
            .FirstOrDefault(e => e.IsVerified && e.UserId != userId);

        // 2. CrossUserBlocked: another user verified-holds the claim and the claim is unverified.
        if (blocker is not null && !claimEmailVerified)
        {
            var blockerRows = await repository.GetUserEmailsByUserIdForMutationAsync(
                blocker.UserId, cancellationToken);

            var blockedDescription = await BuildCrossUserDiagnosticAsync(
                kind: "BLOCKED (unverified provider claim)",
                userId, signingRows: rows,
                provider, providerKey, claimEmail, claimEmailVerified,
                previousEmail: tagged?.Email,
                displaced: blocker,
                displacedRows: blockerRows,
                displacedUserLeftWithoutVerifiedEmail: false,
                cancellationToken);

            logger.LogError(
                "OAuth cross-user collision BLOCKED (unverified claim): {Description}",
                blockedDescription);

            await auditLogService.LogAsync(
                UsersAuditActions.OAuthRenameCollisionBlocked,
                AuditSubjectTypes.User, userId,
                blockedDescription,
                actorUserId: userId,
                relatedEntityId: blocker.Id, relatedEntityType: AuditSubjectTypes.UserEmail);

            return new OAuthReconcileResult(
                ReconcileOutcome.CrossUserBlocked,
                PreviousEmail: tagged?.Email,
                AffectedRowId: null,
                DisplacedUserId: blocker.UserId,
                DisplacedRowId: blocker.Id,
                DisplacedEmail: blocker.Email,
                DisplacedUserLeftWithoutVerifiedEmail: false);
        }

        // 3. Decide cross-user displacement (snapshot only; commits with step 5).
        var displaced = false;
        Guid? displacedUserId = null;
        Guid? displacedRowId = null;
        string? displacedEmail = null;
        var displacedUserLeftWithoutVerifiedEmail = false;
        string? collisionDiagnostic = null;

        if (blocker is not null && claimEmailVerified)
        {
            var displacedUsersRows = await repository.GetUserEmailsByUserIdForMutationAsync(
                blocker.UserId, cancellationToken);
            displacedUserLeftWithoutVerifiedEmail = displacedUsersRows
                .Count(r => r.IsVerified && r.Id != blocker.Id) == 0;

            displaced = true;
            displacedUserId = blocker.UserId;
            displacedRowId = blocker.Id;
            displacedEmail = blocker.Email;

            collisionDiagnostic = await BuildCrossUserDiagnosticAsync(
                kind: "DISPLACED",
                userId, signingRows: rows,
                provider, providerKey, claimEmail, claimEmailVerified,
                previousEmail: tagged?.Email,
                displaced: blocker,
                displacedRows: displacedUsersRows,
                displacedUserLeftWithoutVerifiedEmail: displacedUserLeftWithoutVerifiedEmail,
                cancellationToken);
        }

        // 4. Build the signing user's mutation plan (in memory).
        ReconcileOutcome outcome;
        Guid affectedRowId;
        string? previousEmail = tagged?.Email;
        AuditActionDefinition signingAction;
        string signingDescription;

        UserEmail? rowToUpdate = null;
        UserEmail? rowToDelete = null;
        UserEmail? rowToInsert = null;

        if (tagged is not null && siblingAtClaim is not null)
        {
            // Tag-move: old-tagged row + sibling holds claim email.
            siblingAtClaim.Provider = provider;
            siblingAtClaim.ProviderKey = providerKey;
            siblingAtClaim.IsVerified = true;
            siblingAtClaim.IsPrimary = siblingAtClaim.IsPrimary || tagged.IsPrimary;
            siblingAtClaim.UpdatedAt = now;
            tagged.IsPrimary = false;

            rowToUpdate = siblingAtClaim;
            rowToDelete = tagged;

            outcome = ReconcileOutcome.TagMoved;
            affectedRowId = siblingAtClaim.Id;
            signingAction = UsersAuditActions.GoogleEmailRenamed;
            signingDescription =
                $"OAuth tag moved from `{previousEmail}` to `{siblingAtClaim.Email}` " +
                $"(sub={providerKey}); old row deleted, flags unioned onto matching row.";
        }
        else if (tagged is not null)
        {
            // Rewrite in place — no sibling at claim email.
            tagged.Email = claimEmail;
            tagged.IsVerified = true;
            tagged.UpdatedAt = now;

            rowToUpdate = tagged;

            outcome = ReconcileOutcome.EmailRewritten;
            affectedRowId = tagged.Id;
            signingAction = UsersAuditActions.GoogleEmailRenamed;
            signingDescription =
                $"OAuth email renamed `{previousEmail}` -> `{claimEmail}` " +
                $"(sub={providerKey}).";
        }
        else if (siblingAtClaim is not null)
        {
            // Attach tag to existing row (legacy backfill / first OAuth on plain-email account).
            siblingAtClaim.Provider = provider;
            siblingAtClaim.ProviderKey = providerKey;
            siblingAtClaim.IsVerified = true;
            siblingAtClaim.UpdatedAt = now;

            rowToUpdate = siblingAtClaim;

            outcome = ReconcileOutcome.TagMoved;
            affectedRowId = siblingAtClaim.Id;
            signingAction = UsersAuditActions.GoogleEmailRenamed;
            signingDescription =
                $"OAuth tag attached to existing row `{siblingAtClaim.Email}` " +
                $"(sub={providerKey}).";
        }
        else
        {
            rowToInsert = new UserEmail
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Email = claimEmail,
                IsVerified = true,
                IsPrimary = false,
                Provider = provider,
                ProviderKey = providerKey,
                CreatedAt = now,
                UpdatedAt = now,
            };

            outcome = ReconcileOutcome.NewRowCreated;
            affectedRowId = rowToInsert.Id;
            signingAction = UsersAuditActions.UserEmailLinked;
            signingDescription =
                $"OAuth callback: created UserEmail row " +
                $"(provider={provider}, sub={providerKey}, email={claimEmail}).";
        }

        // 5. Apply displacement + signing-user mutation atomically and repair UserEmail invariants.
        await userService.ApplyUserEmailReconcilePlanAsync(
            userId,
            new UserEmailReconcilePlanCommand(
                DisplacedRowToDelete: displaced ? blocker : null,
                RowToDelete: rowToDelete,
                RowToUpdate: rowToUpdate,
                RowToInsert: rowToInsert),
            cancellationToken);

        // Addresses that left an account: a tag-move deletes the old row, a rewrite renames it in
        // place, a displacement deletes the other user's row. Each stops being a Google identity.
        if (previousEmail is not null && outcome is ReconcileOutcome.TagMoved or ReconcileOutcome.EmailRewritten
            && !string.Equals(previousEmail, claimEmail, StringComparison.OrdinalIgnoreCase))
            await AddressRemovedAsync(userId, previousEmail, cancellationToken);
        if (displaced)
            await AddressRemovedAsync(blocker!.UserId, blocker.Email, cancellationToken);

        // 6. Audit. A displacement writes the OAuthRenameCollision pair instead.
        if (displaced)
        {
            logger.LogError(
                "OAuth cross-user displacement (verified claim): {Description}",
                collisionDiagnostic);

            await auditLogService.LogAsync(
                UsersAuditActions.OAuthRenameCollision,
                AuditSubjectTypes.User, userId,
                collisionDiagnostic!,
                actorUserId: userId,
                relatedEntityId: blocker!.Id, relatedEntityType: AuditSubjectTypes.UserEmail);

            var displacedAuditDescription =
                $"Row `{blocker.Email}` deleted by OAuth callback for user {userId} " +
                $"(provider={provider}, sub={providerKey})." +
                (displacedUserLeftWithoutVerifiedEmail
                    ? " Displaced user left with zero verified emails."
                    : string.Empty);
            await auditLogService.LogAsync(
                UsersAuditActions.UserEmailDisplacedByOAuthRename,
                AuditSubjectTypes.User, blocker.UserId,
                displacedAuditDescription,
                actorUserId: userId,
                relatedEntityId: blocker.Id, relatedEntityType: AuditSubjectTypes.UserEmail);

            return new OAuthReconcileResult(
                ReconcileOutcome.CrossUserDisplaced,
                previousEmail,
                affectedRowId,
                displacedUserId,
                displacedRowId,
                displacedEmail,
                displacedUserLeftWithoutVerifiedEmail);
        }

        await auditLogService.LogAsync(
            signingAction,
            AuditSubjectTypes.User, userId,
            signingDescription,
            actorUserId: userId,
            relatedEntityId: affectedRowId, relatedEntityType: AuditSubjectTypes.UserEmail);

        return new OAuthReconcileResult(
            outcome,
            previousEmail,
            affectedRowId,
            displacedUserId,
            displacedRowId,
            displacedEmail,
            displacedUserLeftWithoutVerifiedEmail);
    }

    private async Task<string> BuildCrossUserDiagnosticAsync(
        string kind,
        Guid signingUserId,
        IReadOnlyList<UserEmail> signingRows,
        string provider,
        string providerKey,
        string claimEmail,
        bool claimEmailVerified,
        string? previousEmail,
        UserEmail displaced,
        IReadOnlyList<UserEmail> displacedRows,
        bool displacedUserLeftWithoutVerifiedEmail,
        CancellationToken ct)
    {
        var loginsByUser = await userService.GetExternalLoginsByUserIdsAsync([signingUserId, displaced.UserId], ct);
        var signingLogins = loginsByUser.TryGetValue(signingUserId, out var sl) ? sl : [];
        var displacedLogins = loginsByUser.TryGetValue(displaced.UserId, out var dl) ? dl : [];

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder();
        sb.Append(inv, $"OAuth cross-user {kind}: provider={provider} sub={providerKey} ");
        sb.Append(inv, $"claimEmail={claimEmail} claimEmailVerified={claimEmailVerified} ");
        sb.Append(inv, $"previousEmail={previousEmail ?? "(none)"} attemptedAt={clock.GetCurrentInstant()}. ");
        sb.Append(inv, $"SigningUser={signingUserId} rows=[");
        sb.Append(string.Join(", ", signingRows.Select(FormatRow)));
        sb.Append("] logins=[");
        sb.Append(string.Join(", ", signingLogins.Select(l => $"{l.Provider}/{l.ProviderKey}")));
        sb.Append(inv, $"]. DisplacedUser={displaced.UserId} displacedRowId={displaced.Id} ");
        sb.Append(inv, $"displacedEmail={displaced.Email}");
        if (displacedUserLeftWithoutVerifiedEmail)
            sb.Append(" — displaced user left with zero verified emails after delete");
        sb.Append(" rows=[");
        sb.Append(string.Join(", ", displacedRows.Select(FormatRow)));
        sb.Append("] logins=[");
        sb.Append(string.Join(", ", displacedLogins.Select(l => $"{l.Provider}/{l.ProviderKey}")));
        sb.Append("].");
        return sb.ToString();

        static string FormatRow(UserEmail r) =>
            $"{{Id={r.Id} Email={r.Email} V={r.IsVerified} P={r.IsPrimary} " +
            $"Provider={r.Provider ?? "(none)"} Key={r.ProviderKey ?? "(none)"}}}";
    }

    private const string EmailVerificationTokenPurpose = "UserEmailVerification";

    public async Task<AddEmailResult> AddEmailAsync(
        Guid userId, string email, CancellationToken cancellationToken = default, Guid? actorUserId = null)
    {
        email = email.Trim();
        var normalizedEmail = EmailNormalization.NormalizeForComparison(email);
        var alternateEmail = EmailLookupAliases.GetAlternate(normalizedEmail);

        if (!new EmailAddressAttribute().IsValid(email))
            return Refuse("Validation_EmailAddress");

        var held = await repository.GetUserEmailsByAddressAsync(normalizedEmail, alternateEmail, cancellationToken);
        if (held.Any(e => e.UserId == userId))
            return Refuse("Users_Profile_EmailAlreadyAdded");

        if (await mergeRepository.HasPendingForUserAndEmailAsync(
                userId, normalizedEmail, alternateEmail, cancellationToken))
            return Refuse("Users_Profile_EmailMergePending");

        // FindByIdAsync, not cache: UserManager token APIs read SecurityStamp directly and cache-rehydrated Users lack it.
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null)
            return Refuse("Users_EmailUpdate_AccountUnavailable");

        // Through IUserManagement so its caching decorator refreshes UserInfo.
        var addResult = await userService.AddUserEmailAsync(
            userId,
            new UserEmailAddCommand(
                email,
                IsVerified: false,
                VerificationSentAt: clock.GetCurrentInstant(),
                IgnoreExisting: true),
            cancellationToken);

        if (addResult.ErrorKey is { } errorKey)
            return new AddEmailResult(Guid.Empty, string.Empty, false, errorKey);

        if (!addResult.Added)
            return Refuse("Users_Profile_EmailAlreadyAdded");

        // The row committed: finish its audit and verification queue even after a disconnect.
        cancellationToken = CancellationToken.None;
        var description = $"Added pending email {email} (conflict: {addResult.IsConflict})";
        if (actorUserId is { } actor)
            await auditLogService.LogAsync(UsersAuditActions.UserEmailAdded, AuditSubjectTypes.User, userId,
                description, actor, relatedEntityId: addResult.EmailId, relatedEntityType: AuditSubjectTypes.UserEmail);
        else
            await auditLogService.LogAsync(UsersAuditActions.UserEmailAdded, AuditSubjectTypes.User, userId,
                description, nameof(UserEmailService), relatedEntityId: addResult.EmailId, relatedEntityType: AuditSubjectTypes.UserEmail);

        var token = await userManager.GenerateUserTokenAsync(
            user,
            TokenOptions.DefaultEmailProvider,
            $"{EmailVerificationTokenPurpose}:{addResult.EmailId}");

        var baseUrl = emailSettings.Value.BaseUrl;
        var verificationUrl = QueryHelpers.AddQueryString($"{baseUrl.TrimEnd('/')}/Profile/Me/Emails/Verify",
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["userId"] = userId.ToString(),
                ["emailId"] = addResult.EmailId.ToString(),
                ["token"] = HttpUtility.UrlEncode(token)
            });
        var info = await userService.GetUserInfoAsync(userId, cancellationToken);
        var message = usersEmails.EmailVerification(
            userId, email, info?.BurnerName ?? string.Empty, verificationUrl, addResult.IsConflict,
            user.PreferredLanguage.IsSupportedCultureCode()
                ? user.PreferredLanguage : CultureCatalog.DefaultCultureCode);
        await emailService.SendAsync(message, cancellationToken);

        return new AddEmailResult(addResult.EmailId, token, addResult.IsConflict);

        AddEmailResult Refuse(string key)
        {
            logger.LogWarning("Email addition refused for user {UserId}: {ErrorKey}", userId, key);
            return new AddEmailResult(Guid.Empty, string.Empty, false, key);
        }
    }

    public async Task<VerifyEmailResult> VerifyEmailAsync(
        Guid userId, Guid emailId, string token, CancellationToken cancellationToken = default)
    {
        // Not the cache: a UserInfo-rehydrated User lacks the SecurityStamp that
        // VerifyUserTokenAsync reads.
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null) return Refuse("User not found");

        // The token is bound to this row's Id via the purpose suffix.
        var pendingEmail = await repository.GetUserEmailByIdAndUserIdAsync(emailId, userId, cancellationToken);
        if (pendingEmail is null || pendingEmail.IsVerified || pendingEmail.Provider is not null)
        {
            return Refuse("No email pending verification");
        }

        var isValid = await userManager.VerifyUserTokenAsync(
            user,
            TokenOptions.DefaultEmailProvider,
            $"{EmailVerificationTokenPurpose}:{pendingEmail.Id}",
            token);

        if (!isValid)
            return Refuse("Invalid verification token");

        if (await RequestMergeForVerificationConflictAsync(userId, pendingEmail, cancellationToken))
            return new VerifyEmailResult(pendingEmail.Email, MergeRequestCreated: true);

        // Flipping unverified→verified may newly satisfy the primary invariant.
        var update = await userService.UpdateUserEmailAsync(
            userId,
            pendingEmail.Id,
            new UserEmailUpdateCommand(MarkVerified: true),
            cancellationToken);
        if (!update.Succeeded) return Refuse("Email update refused");

        return new VerifyEmailResult(pendingEmail.Email, MergeRequestCreated: false);

        VerifyEmailResult Refuse(string reason)
        {
            logger.LogWarning("Email verification rejected for user {UserId}, email row {EmailId}: {Reason}",
                userId, emailId, reason);
            return new VerifyEmailResult(string.Empty, false, "Users_Profile_InvalidVerificationLink");
        }
    }

    public async Task<VerifyEmailResult> AdminMarkVerifiedAsync(
        Guid userId, Guid emailId, Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var pendingEmail = await repository.GetUserEmailByIdAndUserIdAsync(emailId, userId, cancellationToken);
        if (pendingEmail is null || pendingEmail.IsVerified || pendingEmail.Provider is not null)
        {
            return new VerifyEmailResult(string.Empty, false) { NoPendingVerification = true };
        }

        // As in VerifyEmailAsync: an address verified on another account becomes a merge request.
        if (await RequestMergeForVerificationConflictAsync(userId, pendingEmail, cancellationToken))
            return new VerifyEmailResult(pendingEmail.Email, MergeRequestCreated: true);

        var update = await userService.UpdateUserEmailAsync(
            userId,
            pendingEmail.Id,
            new UserEmailUpdateCommand(MarkVerified: true),
            cancellationToken);
        if (!update.Succeeded) return new VerifyEmailResult(string.Empty, false, update.ErrorKey);

        await auditLogService.LogAsync(
            UsersAuditActions.UserEmailManuallyVerified,
            AuditSubjectTypes.User, userId,
            $"Admin manually verified email {pendingEmail.Email}",
            actorUserId,
            relatedEntityId: pendingEmail.Id, relatedEntityType: AuditSubjectTypes.UserEmail);

        return new VerifyEmailResult(pendingEmail.Email, MergeRequestCreated: false);
    }

    // Reads the table, not the cache: the decision must see this call's row state. Any verified
    // row for the same address on another row id, either user's, is the conflict.
    private async Task<bool> RequestMergeForVerificationConflictAsync(
        Guid userId, UserEmail pendingEmail, CancellationToken cancellationToken)
    {
        var normalized = EmailNormalization.NormalizeForComparison(pendingEmail.Email);
        var rows = await repository.GetUserEmailsByAddressAsync(
            normalized, EmailLookupAliases.GetAlternate(normalized), cancellationToken);
        var conflictingEmail = rows.FirstOrDefault(e => e.IsVerified && e.Id != pendingEmail.Id);
        if (conflictingEmail is null)
            return false;

        // Link prefetch, double-clicks and repeat admin verification would duplicate it.
        if (!await mergeRepository.HasPendingForEmailIdAsync(pendingEmail.Id, cancellationToken))
        {
            var now = clock.GetCurrentInstant();
            var mergeRequest = new AccountMergeRequest
            {
                Id = Guid.NewGuid(),
                TargetUserId = userId,
                SourceUserId = conflictingEmail.UserId,
                Email = pendingEmail.Email,
                PendingEmailId = pendingEmail.Id,
                Status = AccountMergeRequestStatus.Pending,
                CreatedAt = now
            };

            await mergeRepository.AddAsync(mergeRequest, cancellationToken);
        }

        return true;
    }
}
