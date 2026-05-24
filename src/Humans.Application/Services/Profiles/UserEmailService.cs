using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Domain.Helpers;
using Humans.Application.Interfaces.Users;
using Humans.Application.Interfaces.Profiles;

namespace Humans.Application.Services.Profiles;

public sealed class UserEmailService(
    IUserEmailRepository repository,
    IUserService userService,
    UserManager<User> userManager,
    IClock clock,
    IAuditLogService auditLogService,
    IServiceProvider serviceProvider,
    ILogger<UserEmailService> logger) : IUserEmailService, IUserMerge
{
    private const string EmailVerificationTokenPurpose = "UserEmailVerification";

    // Lazy: breaks DI cycle TeamService -> IEmailService -> IUserEmailService -> IAccountMergeService -> ITeamService.
    private IAccountMergeService MergeService => serviceProvider.GetRequiredService<IAccountMergeService>();

    // Lazy: breaks DI cycle TicketQueryService -> IUserEmailService -> ITicketServiceRead.
    // Used only by the delete-guard (nobodies-collective/Humans#758) to detect ticket-linked addresses.
    private Interfaces.Tickets.ITicketServiceRead TicketServiceRead =>
        serviceProvider.GetRequiredService<Interfaces.Tickets.ITicketServiceRead>();

    public async Task<IReadOnlyList<UserEmailEditDto>> GetUserEmailsAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var info = await userService.GetUserInfoAsync(userId, cancellationToken);
        if (info is null) return [];

        var emailIds = info.UserEmails.Select(e => e.Id).ToList();
        var mergePendingSet = await MergeService.GetPendingEmailIdsAsync(emailIds, cancellationToken);

        return info.UserEmails.Select(e => new UserEmailEditDto(
            e.Id,
            e.Email,
            e.IsVerified,
            IsGoogle: e.IsGoogle,
            e.Provider,
            e.ProviderKey,
            e.IsPrimary,
            e.Visibility,
            IsPendingVerification: !e.IsVerified && e.VerificationSentAt.HasValue,
            IsMergePending: mergePendingSet.Contains(e.Id)
        )).ToList();
    }

    public async Task<(bool CanAdd, int MinutesUntilResend, Guid? PendingEmailId)>
        GetEmailCooldownInfoAsync(Guid pendingEmailId, CancellationToken ct = default)
    {
        var pendingRecord = await repository.GetByIdReadOnlyAsync(pendingEmailId, ct);

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

        var allowed = GetAllowedVisibilities(accessLevel);

        return info.UserEmails
            .Where(e => e.IsVerified && e.Visibility != null && allowed.Contains(e.Visibility!.Value))
            .OrderBy(e => e.Email, StringComparer.OrdinalIgnoreCase)
            .Select(e => new UserEmailDto(
                e.Id,
                e.Email,
                e.IsVerified,
                IsGoogle: e.IsGoogle,
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
        var alternateEmail = GetAlternateComparableEmail(normalizedEmail);

        if (!new EmailAddressAttribute().IsValid(email))
            throw new ValidationException("Please enter a valid email address.");

        if (await repository.ExistsForUserAsync(userId, normalizedEmail, alternateEmail, cancellationToken))
            throw new ValidationException("This email address is already in your account.");

        if (await MergeService.HasPendingForUserAndEmailAsync(
                userId, normalizedEmail, alternateEmail, cancellationToken))
            throw new ValidationException("A merge request is already pending for this email address.");

        // FindByIdAsync, not cache: UserManager token APIs read SecurityStamp directly and cache-rehydrated Users lack it.
        var user = await userManager.FindByIdAsync(userId.ToString())
            ?? throw new InvalidOperationException("User not found.");

        // Storage mutation is owned by IUserService so its caching decorator can refresh UserInfo.
        var addResult = await userService.AddUserEmailAsync(
            userId,
            new UserEmailAddCommand(
                email,
                IsVerified: false,
                VerificationSentAt: clock.GetCurrentInstant()),
            cancellationToken);

        var token = await userManager.GenerateUserTokenAsync(
            user,
            TokenOptions.DefaultEmailProvider,
            $"{EmailVerificationTokenPurpose}:{addResult.EmailId}");

        return new AddEmailResult(addResult.EmailId, token, addResult.IsConflict);
    }

    public async Task<VerifyEmailResult> VerifyEmailAsync(
        Guid userId, Guid emailId, string token, CancellationToken cancellationToken = default)
    {
        // Cache hits from CachingUserService rehydrate User from UserInfo,
        // which does not carry SecurityStamp. UserManager.VerifyUserTokenAsync
        // (below) reads user.SecurityStamp directly without a DB round-trip, so
        // we must load via UserManager.FindByIdAsync here to get a fully
        // populated entity.
        var user = await userManager.FindByIdAsync(userId.ToString())
            ?? throw new InvalidOperationException("User not found.");

        // see nobodies-collective/Humans#611 — token is bound to this row's Id via the purpose suffix.
        var pendingEmail = await repository.GetByIdAndUserIdAsync(emailId, userId, cancellationToken);
        if (pendingEmail is null || pendingEmail.IsVerified || pendingEmail.Provider is not null)
        {
            throw new ValidationException("No email pending verification.");
        }

        var isValid = await userManager.VerifyUserTokenAsync(
            user,
            TokenOptions.DefaultEmailProvider,
            $"{EmailVerificationTokenPurpose}:{pendingEmail.Id}",
            token);

        if (!isValid)
            throw new ValidationException("The verification link has expired or is invalid.");

        var normalizedPendingEmail = EmailNormalization.NormalizeForComparison(pendingEmail.Email);
        var alternatePendingEmail = GetAlternateComparableEmail(normalizedPendingEmail);
        var conflictingEmail = await repository.GetConflictingVerifiedEmailAsync(
            pendingEmail.Id, normalizedPendingEmail, alternatePendingEmail, cancellationToken);

        if (conflictingEmail is not null)
        {
            // avoid duplicates from link prefetch/double-click
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

            return new VerifyEmailResult(pendingEmail.Email, MergeRequestCreated: true);
        }

        // see nobodies-collective/Humans#687 — flipping unverified→verified may newly satisfy the Google invariant.
        await userService.UpdateUserEmailAsync(
            userId,
            pendingEmail.Id,
            new UserEmailUpdateCommand(MarkVerified: true),
            cancellationToken);

        return new VerifyEmailResult(pendingEmail.Email, MergeRequestCreated: false);
    }

    public async Task<VerifyEmailResult> AdminMarkVerifiedAsync(
        Guid userId, Guid emailId, Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var pendingEmail = await repository.GetByIdAndUserIdAsync(emailId, userId, cancellationToken);
        if (pendingEmail is null || pendingEmail.IsVerified || pendingEmail.Provider is not null)
        {
            throw new ValidationException("No email pending verification.");
        }

        // Mirror VerifyEmailAsync duplicate-handling: create merge request when address verified on another account.
        var normalizedPendingEmail = EmailNormalization.NormalizeForComparison(pendingEmail.Email);
        var alternatePendingEmail = GetAlternateComparableEmail(normalizedPendingEmail);
        var conflictingEmail = await repository.GetConflictingVerifiedEmailAsync(
            pendingEmail.Id, normalizedPendingEmail, alternatePendingEmail, cancellationToken);

        if (conflictingEmail is not null)
        {
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

            return new VerifyEmailResult(pendingEmail.Email, MergeRequestCreated: true);
        }

        // see nobodies-collective/Humans#687
        await userService.UpdateUserEmailAsync(
            userId,
            pendingEmail.Id,
            new UserEmailUpdateCommand(MarkVerified: true),
            cancellationToken);

        await auditLogService.LogAsync(
            AuditAction.UserEmailManuallyVerified,
            nameof(User), userId,
            $"Admin manually verified email {pendingEmail.Email}",
            actorUserId,
            relatedEntityId: pendingEmail.Id, relatedEntityType: nameof(UserEmail));

        return new VerifyEmailResult(pendingEmail.Email, MergeRequestCreated: false);
    }

    public async Task SetPrimaryAsync(
        Guid userId, Guid emailId, CancellationToken cancellationToken = default)
    {
        await userService.UpdateUserEmailAsync(
            userId,
            emailId,
            new UserEmailUpdateCommand(Primary: UserEmailPrimaryChange.MakePrimary),
            cancellationToken);
    }

    public async Task SetVisibilityAsync(
        Guid userId, Guid emailId, ContactFieldVisibility? visibility,
        CancellationToken cancellationToken = default)
    {
        await userService.UpdateUserEmailAsync(
            userId,
            emailId,
            new UserEmailUpdateCommand(ChangeVisibility: true, Visibility: visibility),
            cancellationToken);
    }

    public async Task<bool> DeleteEmailAsync(
        Guid userId, Guid emailId, CancellationToken cancellationToken = default)
    {
        var email = await repository.GetByIdAndUserIdAsync(emailId, userId, cancellationToken)
            ?? throw new InvalidOperationException("Email not found.");

        if (!string.IsNullOrEmpty(email.Provider))
        {
            // Provider-attached rows must go through UnlinkAsync; this guards non-UI callers.
            return false;
        }

        // see nobodies-collective/Humans#758 — block removal of the primary email.
        // The user must promote another verified email to primary first, otherwise they
        // can accidentally delete their sign-in/notification target (the original incident).
        if (email.IsPrimary)
        {
            throw new ValidationException(
                "Cannot remove your primary email. Set another verified email as primary first, " +
                "then remove this one.");
        }

        // Block deletion of the last verified row — post email-decoupling, base.Email is null and the user would silently lose all notifications.
        if (email.IsVerified)
        {
            var allEmails = await repository.GetByUserIdForMutationAsync(userId, cancellationToken);
            var verifiedRemaining = allEmails.Count(e => e.IsVerified && e.Id != emailId);

            if (verifiedRemaining == 0)
            {
                throw new ValidationException(
                    "Cannot remove your last verified email. Add another verified email first " +
                    "so you can still receive system notifications.");
            }
        }

        // see nobodies-collective/Humans#758 — block removal of a ticket-linked email.
        // Tickets are matched to users by email address; removing the linked address risks
        // un-matching the user's event ticket on the next vendor re-sync.
        if (await IsAddressTicketLinkedAsync(userId, email.Email, cancellationToken))
        {
            throw new ValidationException(
                "Cannot remove this email — it is linked to your event ticket. " +
                "Removing it could disconnect your ticket. Contact an admin if you need to change it.");
        }

        return await userService.RemoveUserEmailAsync(
            userId,
            emailId,
            new UserEmailRemoveCommand(UserEmailRemovalMode.PlainEmail),
            cancellationToken);
    }

    public Task RemoveAllEmailsAsync(
        Guid userId, CancellationToken cancellationToken = default) =>
        repository.RemoveAllForUserAsync(userId, cancellationToken);

    /// <inheritdoc />
    public async Task ReassignAsync(Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now,
        CancellationToken ct)
    {
        // Caller invalidates cache AFTER the ambient TransactionScope commits — see AccountMergeService.AcceptAsync.
        await repository.ReassignToUserAsync(
            mergedFromUserId, mergedToUserId, now, ct);
    }

    public async Task<bool> AddVerifiedEmailAsync(
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

        return result.Added;
    }

    [Obsolete("Issue nobodies-collective/Humans#687: User.GoogleEmail is being deprecated. UserEmailService.EnsureGoogleInvariantAsync now stamps IsGoogle on the canonical row whenever a UserEmail is added; no separate backfill is needed. Method body is now a no-op.")]
    public Task<bool> TryBackfillGoogleEmailAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        // no-op — see nobodies-collective/Humans#687 (EnsureGoogleInvariantAsync now owns the invariant).
        _ = userId;
        _ = cancellationToken;
        return Task.FromResult(false);
    }

    public async Task<string?> GetNobodiesTeamEmailAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var info = await userService.GetUserInfoAsync(userId, cancellationToken);
        return info?.UserEmails
            .FirstOrDefault(e => e.IsVerified
                && e.Email.EndsWith("@nobodies.team", StringComparison.OrdinalIgnoreCase))
            ?.Email;
    }

    public async Task<bool> HasNobodiesTeamEmailAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var info = await userService.GetUserInfoAsync(userId, cancellationToken);
        return info?.UserEmails.Any(e => e.IsVerified
            && e.Email.EndsWith("@nobodies.team", StringComparison.OrdinalIgnoreCase)) ?? false;
    }

    public Task<string?> GetVerifiedEmailAddressAsync(
        Guid userId, Guid emailId, CancellationToken cancellationToken = default) =>
        repository.GetVerifiedEmailAddressAsync(userId, emailId, cancellationToken);

    public async Task<Dictionary<Guid, bool>> GetNobodiesTeamEmailStatusByUserAsync(
        CancellationToken cancellationToken = default)
    {
        var infos = await userService.GetAllUserInfosAsync(cancellationToken);
        var result = new Dictionary<Guid, bool>();
        foreach (var info in infos)
        {
            var nobodies = info.UserEmails
                .Where(e => e.IsVerified
                    && e.Email.EndsWith("@nobodies.team", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (nobodies.Count == 0) continue;
            result[info.Id] = nobodies.Any(e => e.IsPrimary);
        }
        return result;
    }

    public async Task<Dictionary<Guid, string>> GetNobodiesTeamEmailsByUserIdsAsync(
        IEnumerable<Guid> userIds, CancellationToken cancellationToken = default)
    {
        var userIdSet = userIds.ToHashSet();
        if (userIdSet.Count == 0)
            return new Dictionary<Guid, string>();

        var infos = await userService.GetUserInfosAsync(userIdSet.ToList(), cancellationToken);
        var result = new Dictionary<Guid, string>();
        foreach (var (uid, info) in infos)
        {
            // Primary-first then any verified — same ordering as the prior repo-driven query (IsPrimary desc, CreatedAt asc).
            var pick = info.UserEmails
                .Where(e => e.IsVerified
                    && e.Email.EndsWith("@nobodies.team", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => e.IsPrimary)
                .Select(e => e.Email)
                .FirstOrDefault();
            if (pick is not null)
                result[uid] = pick;
        }
        return result;
    }

    public async Task<IReadOnlyDictionary<Guid, string>> GetNotificationTargetEmailsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default)
    {
        if (userIds.Count == 0)
            return new Dictionary<Guid, string>();

        var allNotificationTargets = await repository.GetAllNotificationTargetEmailsAsync(cancellationToken);

        var result = new Dictionary<Guid, string>(userIds.Count);
        foreach (var userId in userIds)
        {
            if (allNotificationTargets.TryGetValue(userId, out var email))
                result[userId] = email;
        }

        // Fall back to User.Email (Identity) for users without a notification-target row.
        var missing = userIds.Where(id => !result.ContainsKey(id)).ToList();
        if (missing.Count > 0)
        {
            var users = await userService.GetUserInfosAsync(missing, cancellationToken);
            foreach (var userId in missing)
            {
                if (users.TryGetValue(userId, out var user) && !string.IsNullOrEmpty(user.Email))
                    result[userId] = user.Email;
            }
        }

        return result;
    }

    public async Task<UserEmailWithUser?> FindVerifiedEmailWithUserAsync(
        string email, CancellationToken cancellationToken = default)
    {
        var normalizedEmail = EmailNormalization.NormalizeForComparison(email);
        var alternateEmail = GetAlternateComparableEmail(normalizedEmail);
        return await repository.FindVerifiedWithUserAsync(normalizedEmail, alternateEmail, cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> GetDistinctVerifiedUserIdsAsync(
        string email, CancellationToken cancellationToken = default)
    {
        var normalizedEmail = EmailNormalization.NormalizeForComparison(email);
        var alternateEmail = GetAlternateComparableEmail(normalizedEmail);
        return await repository.GetDistinctVerifiedUserIdsAsync(normalizedEmail, alternateEmail, cancellationToken);
    }

    public Task<Guid?> GetUserIdByVerifiedEmailAsync(
        string email, CancellationToken cancellationToken = default) =>
        repository.GetUserIdByVerifiedEmailAsync(email, cancellationToken);

    public Task<IReadOnlyList<Guid>> GetUserIdsByEmailPrefixAndSuffixAsync(
        string prefix,
        string suffix,
        CancellationToken cancellationToken = default) =>
        repository.GetUserIdsByEmailPrefixAndSuffixAsync(prefix, suffix, cancellationToken);

    public async Task<Guid?> GetUserIdByExactEmailAsync(string email, CancellationToken ct = default)
    {
        // Returns null on zero matches OR ambiguous matches; only non-null when exactly one user verified-holds the address.
        var userIds = await repository.GetDistinctUserIdsByVerifiedEmailAsync(email, ct);
        return userIds.Count == 1 ? userIds[0] : null;
    }

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
        var info = await userService.GetUserInfoAsync(userId, cancellationToken);
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
            email.IsGoogle,
            email.IsPrimary,
            email.Visibility,
            email.VerificationSentAt,
            email.CreatedAt,
            email.UpdatedAt);

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<UserEmailRowSnapshot>>> GetEntitiesByUserIdsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default)
    {
        if (userIds.Count == 0)
            return new Dictionary<Guid, IReadOnlyList<UserEmailRowSnapshot>>();

        var infos = await userService.GetUserInfosAsync(userIds, cancellationToken);
        var result = new Dictionary<Guid, IReadOnlyList<UserEmailRowSnapshot>>(infos.Count);
        foreach (var (uid, info) in infos)
        {
            if (info.UserEmails.Count == 0) continue;
            result[uid] = info.UserEmails.Select(e => ToSnapshot(uid, e)).ToList();
        }
        return result;
    }

    public async Task<IReadOnlyDictionary<Guid, string>> GetNotificationEmailsByUserIdsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default)
    {
        if (userIds.Count == 0)
            return new Dictionary<Guid, string>();

        var all = await repository.GetAllNotificationTargetEmailsAsync(cancellationToken);
        return all
            .Where(kv => userIds.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    public Task<IReadOnlyList<Guid>> SearchUserIdsByVerifiedEmailAsync(
        string searchTerm, CancellationToken cancellationToken = default)
        => repository.SearchUserIdsByVerifiedEmailAsync(searchTerm, cancellationToken);

    public Task<Guid?> GetOtherUserIdHavingEmailAsync(
        string email, Guid excludeUserId, CancellationToken cancellationToken = default)
        => repository.GetOtherUserIdHavingEmailAsync(email, excludeUserId, cancellationToken);

    public Task<bool> IsEmailLinkedToAnyUserAsync(
        string email, CancellationToken cancellationToken = default) =>
        repository.AnyWithEmailAsync(email, cancellationToken);

    public async Task<IReadOnlyList<UserEmailMatch>> MatchByEmailsAsync(
        IReadOnlyCollection<string> emails, CancellationToken cancellationToken = default)
    {
        var rows = await repository.GetByEmailsAsync(emails, cancellationToken);
        return rows
            .Select(r => new UserEmailMatch(
                r.Email, r.UserId, r.IsPrimary, r.IsVerified, r.UpdatedAt))
            .ToList();
    }


    private static List<ContactFieldVisibility> GetAllowedVisibilities(ContactFieldVisibility accessLevel) =>
        accessLevel switch
        {
            ContactFieldVisibility.BoardOnly =>
            [
                ContactFieldVisibility.BoardOnly,
                ContactFieldVisibility.CoordinatorsAndBoard,
                ContactFieldVisibility.MyTeams,
                ContactFieldVisibility.AllActiveProfiles
            ],
            ContactFieldVisibility.CoordinatorsAndBoard =>
            [
                ContactFieldVisibility.CoordinatorsAndBoard,
                ContactFieldVisibility.MyTeams,
                ContactFieldVisibility.AllActiveProfiles
            ],
            ContactFieldVisibility.MyTeams =>
            [
                ContactFieldVisibility.MyTeams,
                ContactFieldVisibility.AllActiveProfiles
            ],
            _ => [ContactFieldVisibility.AllActiveProfiles]
        };

    private static string? GetAlternateComparableEmail(string normalizedEmail)
    {
        if (normalizedEmail.EndsWith("@gmail.com", StringComparison.Ordinal))
            return $"{normalizedEmail[..^"@gmail.com".Length]}@googlemail.com";

        if (normalizedEmail.EndsWith("@googlemail.com", StringComparison.Ordinal))
            return $"{normalizedEmail[..^"@googlemail.com".Length]}@gmail.com";

        return null;
    }

    // see nobodies-collective/Humans#758 — true when the address matches one of the user's
    // ticket emails (order buyer or matched attendee). Reads the Tickets section through its
    // owning read interface (design-rules §9); IUserEmail row data is never read cross-section.
    private async Task<bool> IsAddressTicketLinkedAsync(
        Guid userId, string address, CancellationToken cancellationToken)
    {
        var orders = await TicketServiceRead.GetTicketOrdersAsync(cancellationToken);

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
        // Idempotent — retried imports must not re-add the row.
        await userService.AddUserEmailAsync(
            userId,
            new UserEmailAddCommand(
                email,
                IsVerified: true,
                IgnoreExisting: true),
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Guid?> FindAnyUserIdByEmailAsync(
        string email, CancellationToken cancellationToken = default)
    {
        var normalizedEmail = EmailNormalization.NormalizeForComparison(email);
        var alternateEmail = GetAlternateComparableEmail(normalizedEmail);
        var match = await repository.FindByNormalizedEmailAsync(
            normalizedEmail, alternateEmail, cancellationToken);
        return match?.UserId;
    }

    /// <inheritdoc />
    public async Task<(Guid UserId, Guid EmailId)?> FindAnyEmailRowByAddressAsync(
        string email, CancellationToken cancellationToken = default)
    {
        var normalizedEmail = EmailNormalization.NormalizeForComparison(email);
        var alternateEmail = GetAlternateComparableEmail(normalizedEmail);
        var match = await repository.FindByNormalizedEmailAsync(
            normalizedEmail, alternateEmail, cancellationToken);
        if (match is null) return null;
        return (match.UserId, match.Id);
    }

    /// <inheritdoc />
    public async Task<bool> SetGoogleAsync(
        Guid userId, Guid userEmailId, Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var row = await repository.GetByIdAndUserIdAsync(userEmailId, userId, cancellationToken);
        if (row is null || !row.IsVerified) return false;

        // Capture previous Google email for audit description.
        var allEmails = await repository.GetByUserIdReadOnlyAsync(userId, cancellationToken);
        var previousGoogle = allEmails.FirstOrDefault(e => e.IsGoogle && e.Id != row.Id);

        var updated = await userService.UpdateUserEmailAsync(
            userId,
            row.Id,
            new UserEmailUpdateCommand(Google: UserEmailGoogleChange.MakeGoogle),
            cancellationToken);
        if (!updated) return false;

        var description = previousGoogle is null
            ? $"Set Google identity to {row.Email}"
            : $"Set Google identity to {row.Email} (was {previousGoogle.Email})";

        await auditLogService.LogAsync(
            AuditAction.UserEmailGoogleSet,
            nameof(User), userId,
            description,
            actorUserId,
            relatedEntityId: row.Id, relatedEntityType: nameof(UserEmail));

        return true;
    }

    /// <inheritdoc />
    public async Task<bool> ClearGoogleAsync(
        Guid userId, Guid userEmailId, Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var row = await repository.GetByIdAndUserIdAsync(userEmailId, userId, cancellationToken);
        if (row is null || !row.IsGoogle) return false;

        // Only allow clearing IsGoogle on a duplicate — clearing the sole IsGoogle row → ZeroIsGoogle violation.
        var allEmails = await repository.GetByUserIdReadOnlyAsync(userId, cancellationToken);
        if (allEmails.Count(e => e.IsGoogle) <= 1) return false;

        var updated = await userService.UpdateUserEmailAsync(
            userId,
            row.Id,
            new UserEmailUpdateCommand(Google: UserEmailGoogleChange.ClearDuplicateGoogle),
            cancellationToken);
        if (!updated) return false;

        await auditLogService.LogAsync(
            AuditAction.UserEmailGoogleCleared,
            nameof(User), userId,
            $"Cleared Google identity flag from {row.Email}",
            actorUserId,
            relatedEntityId: row.Id, relatedEntityType: nameof(UserEmail));

        return true;
    }

    /// <inheritdoc />
    public async Task<bool> ClearPrimaryAsync(
        Guid userId, Guid userEmailId, Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var row = await repository.GetByIdAndUserIdAsync(userEmailId, userId, cancellationToken);
        if (row is null || !row.IsPrimary) return false;

        // Only allow clearing IsPrimary on a duplicate verified row — must mirror the scanner's verified filter to avoid ZeroIsPrimary.
        var allEmails = await repository.GetByUserIdReadOnlyAsync(userId, cancellationToken);
        if (allEmails.Count(e => e.IsPrimary && e.IsVerified) <= 1) return false;

        var updated = await userService.UpdateUserEmailAsync(
            userId,
            row.Id,
            new UserEmailUpdateCommand(Primary: UserEmailPrimaryChange.ClearDuplicatePrimary),
            cancellationToken);
        if (!updated) return false;
        // No auto-promote — admin is resolving a duplicate and picks the new primary deliberately.
        await auditLogService.LogAsync(
            AuditAction.UserEmailPrimaryCleared,
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
        foreach (var info in infos.OrderBy(i => i.BurnerName, StringComparer.OrdinalIgnoreCase))
        {
            if (info.UserEmails.Count == 0) continue;

            var verified = info.UserEmails.Where(e => e.IsVerified).ToList();
            var isGoogleCount = info.UserEmails.Count(e => e.IsGoogle);
            var verifiedIsGoogleCount = verified.Count(e => e.IsGoogle);
            var verifiedPrimaryCount = verified.Count(e => e.IsPrimary);
            var hasMultipleGoogle = isGoogleCount > 1;
            // Zero-check filters to verified rows — an unverified IsGoogle row is itself a violation.
            var hasZeroGoogle = verified.Count > 0 && verifiedIsGoogleCount == 0;
            var hasPrimaryProblem = verified.Count > 0 && verifiedPrimaryCount != 1;

            if (!hasMultipleGoogle && !hasZeroGoogle && !hasPrimaryProblem) continue;

            violations.Add(new UserEmailFlagViolation(
                info.Id,
                isGoogleCount,
                verified.Count,
                verifiedPrimaryCount,
                hasMultipleGoogle,
                hasZeroGoogle,
                hasPrimaryProblem));
        }

        return violations;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UserEmailOrphan>> GetOrphanUserEmailsAsync(CancellationToken ct = default)
    {
        // Orphans are UserEmail rows whose UserId is missing or merged. Iterating UserInfo can't find rows for
        // non-existent users, so the repo's full-table scan is still required here.
        var allEmails = await repository.GetAllAsync(ct);
        var liveUserIds = (await userService.GetAllUserInfosAsync(ct).ConfigureAwait(false))
            .Where(u => u.MergedToUserId is null)
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
        var row = await repository.GetByIdReadOnlyAsync(emailId, ct);
        if (row is null) return false;

        return await userService.RemoveUserEmailAsync(
            row.UserId,
            emailId,
            new UserEmailRemoveCommand(
                UserEmailRemovalMode.AnyEmail,
                PreserveLastVerifiedEmail: false),
            ct);
    }

    /// <inheritdoc />
    public async Task<bool> UnlinkAsync(
        Guid userId, Guid userEmailId, Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var row = await repository.GetByIdAndUserIdAsync(userEmailId, userId, cancellationToken);
        if (row is null) return false;
        if (string.IsNullOrEmpty(row.Provider) || string.IsNullOrEmpty(row.ProviderKey))
            return false;

        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null) return false;

        // Capture before RemoveAsync may detach the entity.
        var provider = row.Provider;
        var providerKey = row.ProviderKey;
        var email = row.Email;

        var removeLogin = await userManager.RemoveLoginAsync(user, provider, providerKey);
        if (!removeLogin.Succeeded)
        {
            // Hard-fail to keep AspNetUserLogins and user_emails in sync — otherwise user thinks they unlinked but can still sign in.
            logger.LogError(
                "UnlinkAsync: UserManager.RemoveLoginAsync failed for user {UserId} provider {Provider}; aborting unlink to preserve consistency between AspNetUserLogins and user_emails. Errors: {Errors}",
                userId, provider,
                string.Join("; ", removeLogin.Errors.Select(e => $"{e.Code}:{e.Description}")));
            return false;
        }

        var removed = await userService.RemoveUserEmailAsync(
            userId,
            userEmailId,
            new UserEmailRemoveCommand(
                UserEmailRemovalMode.ProviderLinkedEmail,
                PreserveLastVerifiedEmail: false),
            cancellationToken);
        if (!removed) return false;

        await auditLogService.LogAsync(
            AuditAction.UserEmailUnlinked,
            nameof(User), userId,
            $"Unlinked {provider} (key {ShortHash(providerKey)}) from {email}",
            actorUserId,
            relatedEntityId: row.Id, relatedEntityType: nameof(UserEmail));

        return true;
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
        var rows = (await repository.GetByUserIdForMutationAsync(userId, cancellationToken)).ToList();

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

        // Cross-user check before any mutation. Normalize via gmail/googlemail alternate so dot-aliases can't bypass the displacement gate.
        var normalizedClaim = EmailNormalization.NormalizeForComparison(claimEmail);
        var alternateClaim = GetAlternateComparableEmail(normalizedClaim);
        var blocker = await repository.FindOtherUsersVerifiedRowAsync(
            normalizedClaim, alternateClaim, userId, cancellationToken);

        // 2. CrossUserBlocked: another user verified-holds the claim and provider's claim is unverified — no mutation, audit the attempt.
        if (blocker is not null && !claimEmailVerified)
        {
            // Load displaced user rows for diagnostic only (rare path).
            var blockerRows = await repository.GetByUserIdForMutationAsync(
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
                AuditAction.OAuthRenameCollisionBlocked,
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
            var displacedUsersRows = await repository.GetByUserIdForMutationAsync(
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
        AuditAction signingAction;
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
            siblingAtClaim.IsGoogle = siblingAtClaim.IsGoogle || tagged.IsGoogle;
            siblingAtClaim.UpdatedAt = now;
            tagged.IsPrimary = false;
            tagged.IsGoogle = false;

            rowToUpdate = siblingAtClaim;
            rowToDelete = tagged;

            outcome = ReconcileOutcome.TagMoved;
            affectedRowId = siblingAtClaim.Id;
            signingAction = AuditAction.GoogleEmailRenamed;
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
            signingAction = AuditAction.GoogleEmailRenamed;
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
            signingAction = AuditAction.GoogleEmailRenamed;
            signingDescription =
                $"OAuth tag attached to existing row `{siblingAtClaim.Email}` " +
                $"(sub={providerKey}).";
        }
        else
        {
            // Fresh row.
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
            signingAction = AuditAction.UserEmailLinked;
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

        // 6. Audit. Displaced path writes the OAuthRenameCollision pair instead of GoogleEmailRenamed/UserEmailLinked.
        if (displaced)
        {
            logger.LogError(
                "OAuth cross-user displacement (verified claim): {Description}",
                collisionDiagnostic);

            await auditLogService.LogAsync(
                AuditAction.OAuthRenameCollision,
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
                AuditAction.UserEmailDisplacedByOAuthRename,
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

    // Structured diagnostic for cross-user collision audits — captures both users' full state.
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
            $"{{Id={r.Id} Email={r.Email} V={r.IsVerified} P={r.IsPrimary} G={r.IsGoogle} " +
            $"Provider={r.Provider ?? "(none)"} Key={r.ProviderKey ?? "(none)"}}}";
    }
}
