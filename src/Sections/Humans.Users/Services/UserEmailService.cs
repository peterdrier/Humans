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
    private readonly UserEmailReconciliation reconciliation = new(repository, userService, clock, auditLogService, serviceProvider, logger);

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
            $"Unlinked {provider} (key {ShortProviderKeyHash(providerKey)}) from {email}",
            actorUserId,
            relatedEntityId: row.Id, relatedEntityType: nameof(UserEmail));

        return new(true);
    }

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
