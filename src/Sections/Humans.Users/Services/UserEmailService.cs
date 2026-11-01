using Humans.GoogleIntegration.Contracts;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Humans.AuditLog.Contracts;
using Humans.Users.Data.Repositories;
using Humans.Users.Contracts;
using Humans.Base.Helpers;
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
    private const string EmailVerificationTokenPurpose = "UserEmailVerification";

    // Lazy: breaks DI cycle TeamService -> IEmailService -> IUserEmailService -> IAccountMergeService -> ITeamService.
    private IAccountMergeService MergeService => serviceProvider.GetRequiredService<IAccountMergeService>();

    // Lazy: breaks DI cycle TicketQueryService -> IUserEmailService -> ITicketService.
    private Humans.Tickets.Contracts.ITicketService TicketService =>
        serviceProvider.GetRequiredService<Humans.Tickets.Contracts.ITicketService>();

    // Lazy: GoogleIntegration's identity service reads IUserService.
    private IGoogleIntegrationWrite GoogleIdentity => serviceProvider.GetRequiredService<IGoogleIntegrationWrite>();

    public async Task<IReadOnlyList<UserEmailEditDto>> GetUserEmailsAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var info = await userService.GetUserInfoAsync(userId, cancellationToken);
        if (info is null) return [];

        var emailIds = info.UserEmails.Select(e => e.Id).ToList();
        var mergePendingSet = await MergeService.GetPendingEmailIdsAsync(emailIds, cancellationToken);
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
                && verifiedCount - (e.IsVerified ? 1 : 0) >= 1
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

    public async Task<AddEmailResult> AddEmailAsync(
        Guid userId, string email, CancellationToken cancellationToken = default)
    {
        email = email.Trim();
        var normalizedEmail = EmailNormalization.NormalizeForComparison(email);
        var alternateEmail = EmailLookupAliases.GetAlternate(normalizedEmail);

        if (!new EmailAddressAttribute().IsValid(email))
            return Refuse("Validation_EmailAddress");

        var held = await repository.GetUserEmailsByAddressAsync(normalizedEmail, alternateEmail, cancellationToken);
        if (held.Any(e => e.UserId == userId))
            return Refuse("Users_Profile_EmailAlreadyAdded");

        if (await MergeService.HasPendingForUserAndEmailAsync(
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

        var token = await userManager.GenerateUserTokenAsync(
            user,
            TokenOptions.DefaultEmailProvider,
            $"{EmailVerificationTokenPurpose}:{addResult.EmailId}");

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
            throw new ValidationException("No email pending verification.");
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
            nameof(User), userId,
            $"Admin manually verified email {pendingEmail.Email}",
            actorUserId,
            relatedEntityId: pendingEmail.Id, relatedEntityType: nameof(UserEmail));

        return new VerifyEmailResult(pendingEmail.Email, MergeRequestCreated: false);
    }

    public async Task<UpdateEmailResult> SetPrimaryAsync(
        Guid userId, Guid emailId, CancellationToken cancellationToken = default)
    {
        return await userService.UpdateUserEmailAsync(
            userId,
            emailId,
            new UserEmailUpdateCommand(Primary: UserEmailPrimaryChange.MakePrimary),
            cancellationToken);
    }

    public async Task<UpdateEmailResult> SetVisibilityAsync(
        Guid userId, Guid emailId, ContactFieldVisibility? visibility,
        CancellationToken cancellationToken = default)
    {
        return await userService.UpdateUserEmailAsync(
            userId,
            emailId,
            new UserEmailUpdateCommand(ChangeVisibility: true, Visibility: visibility),
            cancellationToken);
    }

    public async Task<DeleteEmailResult> DeleteEmailAsync(
        Guid userId, Guid emailId, CancellationToken cancellationToken = default)
    {
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
            await GoogleIdentity.ClearIfAddressAsync(userId, email.Email, CancellationToken.None);
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
        Guid userId, string email, CancellationToken cancellationToken = default)
    {
        var result = await userService.AddUserEmailAsync(
            userId,
            new UserEmailAddCommand(
                email,
                IsVerified: true,
                Visibility: ContactFieldVisibility.BoardOnly,
                IgnoreExisting: true),
            cancellationToken);

        return result;
    }

    public async Task<string?> GetNobodiesTeamEmailAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        // Raw: the sole consumer is GDPR erasure, which asks about tombstones. A tombstone owns no
        // addresses, and resolving forward would suspend the living survivor's mailbox.
        var info = await userService.GetRawUserInfoAsync(userId, cancellationToken);
        return info?.UserEmails
            .FirstOrDefault(e => e.IsVerified
                && e.Email.EndsWith("@nobodies.team", StringComparison.OrdinalIgnoreCase))
            ?.Email;
    }

    public async Task<IReadOnlyDictionary<Guid, string>> GetNotificationTargetEmailsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default)
    {
        if (userIds.Count == 0)
            return new Dictionary<Guid, string>();

        // Resolve first: a tombstone's own address is a sentinel that bounces, and its
        // notification rows moved to the survivor. The key stays the requested id.
        var resolved = await userService.GetUserInfosAsync(userIds, cancellationToken);
        var allNotificationTargets = await repository.GetAllNotificationTargetUserEmailsAsync(cancellationToken);

        var result = new Dictionary<Guid, string>(userIds.Count);
        foreach (var userId in userIds)
        {
            if (!resolved.TryGetValue(userId, out var user))
                continue;

            if (allNotificationTargets.TryGetValue(user.Id, out var email))
                result[userId] = email;
            else if (!string.IsNullOrEmpty(user.Email))
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

    public async Task<string?> GetPrimaryEmailAsync(Guid userId, CancellationToken ct = default)
    {
        var info = await userService.GetUserInfoAsync(userId, ct);
        if (info is null) return null;
        var primary = info.UserEmails.FirstOrDefault(e => e.IsVerified && e.IsPrimary);
        if (primary is not null) return primary.Email;
        var anyVerified = info.UserEmails.FirstOrDefault(e => e.IsVerified);
        if (anyVerified is not null) return anyVerified.Email;
        return info.IdentityEmailColumn;
    }


    public async Task<IReadOnlyList<string>> GetVerifiedEmailsForUserAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var info = await userService.GetUserInfoAsync(userId, cancellationToken);
        if (info is null) return [];
        return info.UserEmails
            .Where(e => e.IsVerified)
            .Select(e => e.Email)
            .ToList();
    }

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

        var all = await repository.GetAllNotificationTargetUserEmailsAsync(cancellationToken);
        return all
            .Where(kv => userIds.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
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
        if (!await MergeService.HasPendingForEmailIdAsync(pendingEmail.Id, cancellationToken))
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

            await MergeService.CreateAsync(mergeRequest, cancellationToken);
        }

        return true;
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

    /// <inheritdoc />
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

    /// <inheritdoc />
    public async Task<bool> DeleteByIdAsync(Guid emailId, CancellationToken ct = default)
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
            await GoogleIdentity.ClearIfAddressAsync(row.UserId, row.Email, CancellationToken.None);
        return removed;
    }

    /// <inheritdoc />
    public async Task<UnlinkEmailResult> UnlinkAsync(
        Guid userId, Guid userEmailId, Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var row = await repository.GetUserEmailByIdAndUserIdAsync(userEmailId, userId, cancellationToken);
        if (row is null) return new(false);
        if (string.IsNullOrEmpty(row.Provider) || string.IsNullOrEmpty(row.ProviderKey))
            return new(false);

        // At least one verified email must remain, or the user is locked out (magic link needs one
        // too). Checked even for an unverified row, which can still carry the only OAuth login, and
        // before RemoveLoginAsync so a refusal mutates nothing.
        var allEmails = await repository.GetUserEmailsByUserIdForMutationAsync(userId, cancellationToken);
        var verifiedRemaining = allEmails.Count(e => e.IsVerified && e.Id != userEmailId);
        if (verifiedRemaining == 0)
        {
            logger.LogWarning(
                "Refused unlink of email {EmailId} for user {UserId}: no other verified sign-in method remains",
                userEmailId, userId);
            return new(false, "Users_LinkedAccounts_UnlinkBlockedLastSignInMethod");
        }

        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null) return new(false);

        // Capture before RemoveUserEmailAsync may detach the entity.
        var provider = row.Provider;
        var providerKey = row.ProviderKey;
        var email = row.Email;

        var removeLogin = await userManager.RemoveLoginAsync(user, provider, providerKey);
        if (!removeLogin.Succeeded)
        {
            // Hard-fail, or the user thinks they unlinked but can still sign in.
            logger.LogError(
                "UnlinkAsync: UserManager.RemoveLoginAsync failed for user {UserId} provider {Provider}; aborting unlink to preserve consistency between AspNetUserLogins and user_emails. Errors: {Errors}",
                userId, provider,
                string.Join("; ", removeLogin.Errors.Select(e => $"{e.Code}:{e.Description}")));
            return new(false);
        }

        // Identity removal committed; finish row, invariants, cache and audit even after a disconnect.
        cancellationToken = CancellationToken.None;

        var removed = await userService.RemoveUserEmailAsync(
            userId,
            userEmailId,
            new UserEmailRemoveCommand(
                UserEmailRemovalMode.ProviderLinkedEmail,
                PreserveLastVerifiedEmail: false),
            cancellationToken);
        if (!removed) return new(false);
        await GoogleIdentity.ClearIfAddressAsync(userId, email, cancellationToken);

        await auditLogService.LogAsync(
            UsersAuditActions.UserEmailUnlinked,
            nameof(User), userId,
            $"Unlinked {provider} (key {ShortHash(providerKey)}) from {email}",
            actorUserId,
            relatedEntityId: row.Id, relatedEntityType: nameof(UserEmail));

        return new(true);
    }

    private static string ShortHash(string s)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes.AsSpan(0, 8));
    }

    /// <inheritdoc />
    public async Task<OAuthReconcileResult> ReconcileOAuthIdentityAsync(
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
                nameof(User), userId,
                blockedDescription,
                actorUserId: userId,
                relatedEntityId: blocker.Id, relatedEntityType: nameof(UserEmail));

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
            await GoogleIdentity.ClearIfAddressAsync(userId, previousEmail, cancellationToken);
        if (displaced)
            await GoogleIdentity.ClearIfAddressAsync(blocker!.UserId, blocker.Email, cancellationToken);

        // 6. Audit. A displacement writes the OAuthRenameCollision pair instead.
        if (displaced)
        {
            logger.LogError(
                "OAuth cross-user displacement (verified claim): {Description}",
                collisionDiagnostic);

            await auditLogService.LogAsync(
                UsersAuditActions.OAuthRenameCollision,
                nameof(User), userId,
                collisionDiagnostic!,
                actorUserId: userId,
                relatedEntityId: blocker!.Id, relatedEntityType: nameof(UserEmail));

            var displacedAuditDescription =
                $"Row `{blocker.Email}` deleted by OAuth callback for user {userId} " +
                $"(provider={provider}, sub={providerKey})." +
                (displacedUserLeftWithoutVerifiedEmail
                    ? " Displaced user left with zero verified emails."
                    : string.Empty);
            await auditLogService.LogAsync(
                UsersAuditActions.UserEmailDisplacedByOAuthRename,
                nameof(User), blocker.UserId,
                displacedAuditDescription,
                actorUserId: userId,
                relatedEntityId: blocker.Id, relatedEntityType: nameof(UserEmail));

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
            nameof(User), userId,
            signingDescription,
            actorUserId: userId,
            relatedEntityId: affectedRowId, relatedEntityType: nameof(UserEmail));

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
}
