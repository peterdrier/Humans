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

/// <summary>
/// Service for managing user email addresses. Business logic only —
/// no direct DbContext usage. Cross-section reads (AccountMergeRequests,
/// Users) are routed through their owning service interfaces.
/// </summary>
public sealed class UserEmailService : IUserEmailService, IUserMerge
{
    private readonly IUserEmailRepository _repository;
    private readonly IUserService _userService;
    private readonly UserManager<User> _userManager;
    private readonly IClock _clock;
    private readonly IUserInfoInvalidator _userInfoInvalidator;
    private readonly IAuditLogService _auditLogService;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<UserEmailService> _logger;

    private const string EmailVerificationTokenPurpose = "UserEmailVerification";

    public UserEmailService(
        IUserEmailRepository repository,
        IUserService userService,
        UserManager<User> userManager,
        IClock clock,
        IUserInfoInvalidator userInfoInvalidator,
        IAuditLogService auditLogService,
        IServiceProvider serviceProvider,
        ILogger<UserEmailService> logger)
    {
        _repository = repository;
        _userService = userService;
        _userManager = userManager;
        _clock = clock;
        _userInfoInvalidator = userInfoInvalidator;
        _auditLogService = auditLogService;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    // Lazy to break the DI cycle:
    // TeamService -> IEmailService -> IUserEmailService -> IAccountMergeService -> ITeamService.
    private IAccountMergeService MergeService => _serviceProvider.GetRequiredService<IAccountMergeService>();

    public async Task<IReadOnlyList<UserEmailEditDto>> GetUserEmailsAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var emails = await _repository.GetByUserIdReadOnlyAsync(userId, cancellationToken);

        // Check which emails have pending merge requests (cross-section → IAccountMergeService)
        var emailIds = emails.Select(e => e.Id).ToList();
        var mergePendingSet = await MergeService.GetPendingEmailIdsAsync(emailIds, cancellationToken);

        return emails.Select(e => new UserEmailEditDto(
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

    public async Task<IReadOnlyList<UserEmailDto>> GetVisibleEmailsAsync(
        Guid userId, ContactFieldVisibility accessLevel,
        CancellationToken cancellationToken = default)
    {
        var allowed = GetAllowedVisibilities(accessLevel);
        var allEmails = await _repository.GetByUserIdReadOnlyAsync(userId, cancellationToken);

        var visible = allEmails
            .Where(e => e.IsVerified && e.Visibility != null && allowed.Contains(e.Visibility!.Value))
            .ToList();

        return visible
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

        // Check duplicate for this user
        if (await _repository.ExistsForUserAsync(userId, normalizedEmail, alternateEmail, cancellationToken))
            throw new ValidationException("This email address is already in your account.");

        // Check pending merge (cross-section → IAccountMergeService)
        if (await MergeService.HasPendingForUserAndEmailAsync(
                userId, normalizedEmail, alternateEmail, cancellationToken))
            throw new ValidationException("A merge request is already pending for this email address.");

        // Check conflict for merge flow
        var isConflict = await _repository.ExistsVerifiedForOtherUserAsync(
            userId, normalizedEmail, alternateEmail, cancellationToken);

        // Check same as OAuth login email (cross-section → IUserService)
        var user = await _userService.GetByIdAsync(userId, cancellationToken)
            ?? throw new InvalidOperationException("User not found.");

        if (EmailNormalization.EmailsMatch(email, user.Email))
            throw new ValidationException("This is already your sign-in email.");

        var now = _clock.GetCurrentInstant();

        var userEmail = new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Email = email,
            IsVerified = false,
            IsPrimary = false,
            VerificationSentAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };

        // Issue nobodies-collective/Humans#687: every UserEmail-add path goes
        // through AddRowWithInvariantsAsync — adds the row, runs the Primary +
        // Google invariants, and invalidates UserInfo.
        await AddRowWithInvariantsAsync(userEmail, cancellationToken);

        // Generate verification token via Identity
        var token = await _userManager.GenerateUserTokenAsync(
            user,
            TokenOptions.DefaultEmailProvider,
            $"{EmailVerificationTokenPurpose}:{userEmail.Id}");

        return new AddEmailResult(userEmail.Id, token, isConflict);
    }

    public async Task<VerifyEmailResult> VerifyEmailAsync(
        Guid userId, Guid emailId, string token, CancellationToken cancellationToken = default)
    {
        var user = await _userService.GetByIdAsync(userId, cancellationToken)
            ?? throw new InvalidOperationException("User not found.");

        // Issue nobodies-collective/Humans#611: load the specific pending row
        // the verification link was issued for, not "any non-verified plain
        // row". The token is bound to this row's Id via the purpose suffix
        // ("{EmailVerificationTokenPurpose}:{emailId}"), so picking a
        // different row would cause token validation to fail against the
        // wrong row even when the token is valid.
        var pendingEmail = await _repository.GetByIdAndUserIdAsync(emailId, userId, cancellationToken);
        if (pendingEmail is null || pendingEmail.IsVerified || pendingEmail.Provider is not null)
        {
            throw new ValidationException("No email pending verification.");
        }

        var isValid = await _userManager.VerifyUserTokenAsync(
            user,
            TokenOptions.DefaultEmailProvider,
            $"{EmailVerificationTokenPurpose}:{pendingEmail.Id}",
            token);

        if (!isValid)
            throw new ValidationException("The verification link has expired or is invalid.");

        // Check conflict for merge flow
        var normalizedPendingEmail = EmailNormalization.NormalizeForComparison(pendingEmail.Email);
        var alternatePendingEmail = GetAlternateComparableEmail(normalizedPendingEmail);
        var conflictingEmail = await _repository.GetConflictingVerifiedEmailAsync(
            pendingEmail.Id, normalizedPendingEmail, alternatePendingEmail, cancellationToken);

        if (conflictingEmail is not null)
        {
            // Check for existing pending merge (avoid duplicates from link prefetch/double-click)
            if (!await MergeService.HasPendingForEmailIdAsync(pendingEmail.Id, cancellationToken))
            {
                var now = _clock.GetCurrentInstant();
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

        pendingEmail.IsVerified = true;
        pendingEmail.UpdatedAt = _clock.GetCurrentInstant();
        await _repository.UpdateAsync(pendingEmail, cancellationToken);

        // Issue nobodies-collective/Humans#687: when an unverified row flips to
        // verified, the Google identity invariant may now be satisfiable for
        // the first time (for users whose only verified row is the one just
        // verified). Run the invariant here in addition to the
        // creation-time call inside AddRowWithInvariantsAsync.
        await EnsureGoogleInvariantAsync(userId, cancellationToken);
        await _userInfoInvalidator.InvalidateAsync(userId, cancellationToken);

        return new VerifyEmailResult(pendingEmail.Email, MergeRequestCreated: false);
    }

    public async Task<VerifyEmailResult> AdminMarkVerifiedAsync(
        Guid userId, Guid emailId, Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var pendingEmail = await _repository.GetByIdAndUserIdAsync(emailId, userId, cancellationToken);
        if (pendingEmail is null || pendingEmail.IsVerified || pendingEmail.Provider is not null)
        {
            throw new ValidationException("No email pending verification.");
        }

        // Mirror VerifyEmailAsync's duplicate-handling: if the address is
        // already verified on another account, create a merge request rather
        // than silently completing verification — the duplicate-account flow
        // owns reconciliation.
        var normalizedPendingEmail = EmailNormalization.NormalizeForComparison(pendingEmail.Email);
        var alternatePendingEmail = GetAlternateComparableEmail(normalizedPendingEmail);
        var conflictingEmail = await _repository.GetConflictingVerifiedEmailAsync(
            pendingEmail.Id, normalizedPendingEmail, alternatePendingEmail, cancellationToken);

        if (conflictingEmail is not null)
        {
            if (!await MergeService.HasPendingForEmailIdAsync(pendingEmail.Id, cancellationToken))
            {
                var now = _clock.GetCurrentInstant();
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

        pendingEmail.IsVerified = true;
        pendingEmail.UpdatedAt = _clock.GetCurrentInstant();
        await _repository.UpdateAsync(pendingEmail, cancellationToken);

        // Issue nobodies-collective/Humans#687: see VerifyEmailAsync.
        await EnsureGoogleInvariantAsync(userId, cancellationToken);
        await _userInfoInvalidator.InvalidateAsync(userId, cancellationToken);

        await _auditLogService.LogAsync(
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
        var emails = (await _repository.GetByUserIdForMutationAsync(userId, cancellationToken)).ToList();

        var target = emails.FirstOrDefault(e => e.Id == emailId)
            ?? throw new InvalidOperationException("Email not found.");

        if (!target.IsVerified)
            throw new ValidationException("Only verified emails can be the notification target.");

        var now = _clock.GetCurrentInstant();
        var changed = new List<UserEmail>();
        foreach (var email in emails)
        {
            var shouldBePrimary = email.Id == emailId;
            if (email.IsPrimary != shouldBePrimary)
            {
                email.IsPrimary = shouldBePrimary;
                email.UpdatedAt = now;
                changed.Add(email);
            }
        }

        if (changed.Count == 0)
            return;

        await _repository.UpdateBatchAsync(changed, cancellationToken);

        // UserInfo.PrimaryEmail derives from the row with IsPrimary=true.
        await _userInfoInvalidator.InvalidateAsync(userId, cancellationToken);
    }

    public async Task SetVisibilityAsync(
        Guid userId, Guid emailId, ContactFieldVisibility? visibility,
        CancellationToken cancellationToken = default)
    {
        var email = await _repository.GetByIdAndUserIdAsync(emailId, userId, cancellationToken)
            ?? throw new InvalidOperationException("Email not found.");

        email.Visibility = visibility;
        email.UpdatedAt = _clock.GetCurrentInstant();
        await _repository.UpdateAsync(email, cancellationToken);
        await _userInfoInvalidator.InvalidateAsync(userId, cancellationToken);
    }

    public async Task<bool> DeleteEmailAsync(
        Guid userId, Guid emailId, CancellationToken cancellationToken = default)
    {
        var email = await _repository.GetByIdAndUserIdAsync(emailId, userId, cancellationToken)
            ?? throw new InvalidOperationException("Email not found.");

        if (!string.IsNullOrEmpty(email.Provider))
        {
            // Provider-attached rows go through UnlinkAsync (which removes the
            // AspNetUserLogins row and the email row). The per-row UI never
            // routes a Provider-attached row to Delete; this is the service-level
            // guard for non-UI callers.
            return false;
        }

        // Preserve at least one verified UserEmail. An OAuth-only account can still
        // sign in but cannot receive system notifications (the User.Email override
        // falls back to base.Email when no verified UserEmails are loaded, and
        // base.Email is null post email-decoupling), so blocking the last-verified-row
        // delete is preferable to silently dropping notifications. Unverified rows
        // aren't notification targets, so deleting one is safe.
        if (email.IsVerified)
        {
            var allEmails = await _repository.GetByUserIdForMutationAsync(userId, cancellationToken);
            var verifiedRemaining = allEmails.Count(e => e.IsVerified && e.Id != emailId);

            if (verifiedRemaining == 0)
            {
                throw new ValidationException(
                    "Cannot remove your last verified email. Add another verified email first " +
                    "so you can still receive system notifications.");
            }
        }

        await _repository.RemoveAsync(email, cancellationToken);

        // If the removed row was primary, promote the highest-priority remaining
        // verified row (Workspace > most-recently-updated).
        await EnsurePrimaryInvariantAsync(userId, cancellationToken);

        // If the removed row was the IsGoogle row, restamp the canonical
        // remaining @nobodies.team row so the user doesn't drift into the
        // zero-IsGoogle state.
        await EnsureGoogleInvariantAsync(userId, cancellationToken);

        // UserInfo.PrimaryEmail derives from user_emails; drop the stale entry so
        // admin/search/profile surfaces stop showing the removed address.
        await _userInfoInvalidator.InvalidateAsync(userId, cancellationToken);

        return true;
    }

    public Task RemoveAllEmailsAsync(
        Guid userId, CancellationToken cancellationToken = default) =>
        _repository.RemoveAllForUserAsync(userId, cancellationToken);

    /// <inheritdoc />
    public async Task ReassignAsync(Guid mergedFromUserId, Guid mergedToUserId, Guid actorUserId, Instant now,
        CancellationToken ct)
    {
        // Cache invalidation is the caller's responsibility — must run AFTER
        // the ambient TransactionScope completes so a rolled-back fold
        // doesn't repopulate caches from now-uncommitted state.
        // See AccountMergeService.AcceptAsync post-commit block.
        await _repository.ReassignToUserAsync(
            mergedFromUserId, mergedToUserId, now, ct);
    }

    public async Task<bool> AddVerifiedEmailAsync(
        Guid userId, string email, CancellationToken cancellationToken = default)
    {
        var normalizedEmail = EmailNormalization.NormalizeForComparison(email);
        var alternateEmail = GetAlternateComparableEmail(normalizedEmail);

        if (await _repository.ExistsForUserAsync(userId, normalizedEmail, alternateEmail, cancellationToken))
            return false;

        var now = _clock.GetCurrentInstant();

        var userEmail = new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Email = email,
            IsVerified = true,
            // IsPrimary / IsGoogle set below by AddRowWithInvariantsAsync.
            IsPrimary = false,
            Visibility = ContactFieldVisibility.BoardOnly,
            CreatedAt = now,
            UpdatedAt = now
        };

        // Issue nobodies-collective/Humans#687: every UserEmail-add path goes
        // through the orchestrator. EnsureGoogleInvariantAsync stamps IsGoogle
        // on the canonical row (Workspace > existing-IsGoogle > most-recent),
        // so the @nobodies.team row wins automatically without a separate
        // _userService.TrySetGoogleEmailAsync write.
        await AddRowWithInvariantsAsync(userEmail, cancellationToken);
        return true;
    }

    [Obsolete("Issue nobodies-collective/Humans#687: User.GoogleEmail is being deprecated. UserEmailService.EnsureGoogleInvariantAsync now stamps IsGoogle on the canonical row whenever a UserEmail is added; no separate backfill is needed. Method body is now a no-op.")]
    public Task<bool> TryBackfillGoogleEmailAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        // Body intentionally left as a no-op. The Google identity invariant
        // is now maintained by EnsureGoogleInvariantAsync, which is called from
        // AddRowWithInvariantsAsync on every UserEmail row creation and from
        // VerifyEmailAsync / AdminMarkVerifiedAsync on every verification.
        // See issue nobodies-collective/Humans#687.
        _ = userId;
        _ = cancellationToken;
        return Task.FromResult(false);
    }

    public async Task<string?> GetNobodiesTeamEmailAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var all = await _repository.GetAllVerifiedNobodiesTeamEmailsAsync(cancellationToken);
        return all.FirstOrDefault(e => e.UserId == userId)?.Email;
    }

    public async Task<bool> HasNobodiesTeamEmailAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var all = await _repository.GetAllVerifiedNobodiesTeamEmailsAsync(cancellationToken);
        return all.Any(e => e.UserId == userId);
    }

    public Task<string?> GetVerifiedEmailAddressAsync(
        Guid userId, Guid emailId, CancellationToken cancellationToken = default) =>
        _repository.GetVerifiedEmailAddressAsync(userId, emailId, cancellationToken);

    public async Task<Dictionary<Guid, bool>> GetNobodiesTeamEmailStatusByUserAsync(
        CancellationToken cancellationToken = default)
    {
        var all = await _repository.GetAllVerifiedNobodiesTeamEmailsAsync(cancellationToken);
        return all
            .GroupBy(e => e.UserId)
            .ToDictionary(
                g => g.Key,
                g => g.Any(e => e.IsPrimary));
    }

    public async Task<Dictionary<Guid, string>> GetNobodiesTeamEmailsByUserIdsAsync(
        IEnumerable<Guid> userIds, CancellationToken cancellationToken = default)
    {
        var userIdSet = userIds.ToHashSet();
        if (userIdSet.Count == 0)
            return new Dictionary<Guid, string>();

        var all = await _repository.GetAllVerifiedNobodiesTeamEmailsAsync(cancellationToken);
        return all
            .Where(e => userIdSet.Contains(e.UserId))
            .GroupBy(e => e.UserId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(e => e.IsPrimary)
                    .ThenBy(e => e.CreatedAt)
                    .First().Email);
    }

    public async Task<IReadOnlyDictionary<Guid, string>> GetNotificationTargetEmailsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default)
    {
        if (userIds.Count == 0)
            return new Dictionary<Guid, string>();

        // Start with users who have a verified IsPrimary row.
        var allNotificationTargets = await _repository.GetAllNotificationTargetEmailsAsync(cancellationToken);

        var result = new Dictionary<Guid, string>(userIds.Count);
        foreach (var userId in userIds)
        {
            if (allNotificationTargets.TryGetValue(userId, out var email))
                result[userId] = email;
        }

        // For users without a notification-target row, fall back to User.Email
        // (the Identity email). Single-batch round-trip via IUserService.
        var missing = userIds.Where(id => !result.ContainsKey(id)).ToList();
        if (missing.Count > 0)
        {
            var users = await _userService.GetUserInfosAsync(missing, cancellationToken);
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
        return await _repository.FindVerifiedWithUserAsync(normalizedEmail, alternateEmail, cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> GetDistinctVerifiedUserIdsAsync(
        string email, CancellationToken cancellationToken = default)
    {
        var normalizedEmail = EmailNormalization.NormalizeForComparison(email);
        var alternateEmail = GetAlternateComparableEmail(normalizedEmail);
        return await _repository.GetDistinctVerifiedUserIdsAsync(normalizedEmail, alternateEmail, cancellationToken);
    }

    public Task<Guid?> GetUserIdByVerifiedEmailAsync(
        string email, CancellationToken cancellationToken = default) =>
        _repository.GetUserIdByVerifiedEmailAsync(email, cancellationToken);

    public Task<IReadOnlyList<Guid>> GetUserIdsByEmailPrefixAndSuffixAsync(
        string prefix,
        string suffix,
        CancellationToken cancellationToken = default) =>
        _repository.GetUserIdsByEmailPrefixAndSuffixAsync(prefix, suffix, cancellationToken);

    public async Task<Guid?> GetUserIdByExactEmailAsync(string email, CancellationToken ct = default)
    {
        // Returns null on zero matches (unverified-only or unknown address) and
        // on ambiguous matches (the same verified address appears on more than one
        // user — an invariant violation, but we treat it safely). Only returns a
        // non-null id when exactly one distinct UserId owns the verified address.
        var userIds = await _repository.GetDistinctUserIdsByVerifiedEmailAsync(email, ct);
        return userIds.Count == 1 ? userIds[0] : (Guid?)null;
    }

    public async Task<string?> GetPrimaryEmailAsync(Guid userId, CancellationToken ct = default)
    {
        var emails = await _repository.GetByUserIdReadOnlyAsync(userId, ct);
        var primary = emails.FirstOrDefault(e => e.IsVerified && e.IsPrimary);
        if (primary is not null) return primary.Email;
        var anyVerified = emails.FirstOrDefault(e => e.IsVerified);
        if (anyVerified is not null) return anyVerified.Email;
        // Fall back to User.Email as last resort
        var user = await _userService.GetByIdAsync(userId, ct);
        return user?.Email;
    }


    public async Task<IReadOnlyList<string>> GetVerifiedEmailsForUserAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var emails = await _repository.GetByUserIdReadOnlyAsync(userId, cancellationToken);
        return emails
            .Where(e => e.IsVerified)
            .Select(e => e.Email)
            .ToList();
    }

    public async Task<IReadOnlyList<UserEmailRowSnapshot>> GetEntitiesByUserIdAsync(
        Guid userId, CancellationToken cancellationToken = default) =>
        (await _repository.GetByUserIdReadOnlyAsync(userId, cancellationToken))
            .Select(ToSnapshot)
            .ToList();

    private static UserEmailRowSnapshot ToSnapshot(UserEmail email) =>
        new(
            email.Id,
            email.UserId,
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

        var allEmails = await _repository.GetAllAsync(cancellationToken);
        var idSet = new HashSet<Guid>(userIds);
        return allEmails
            .Where(e => idSet.Contains(e.UserId))
            .GroupBy(e => e.UserId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<UserEmailRowSnapshot>)g.Select(ToSnapshot).ToList());
    }

    public async Task<IReadOnlyDictionary<Guid, string>> GetNotificationEmailsByUserIdsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default)
    {
        if (userIds.Count == 0)
            return new Dictionary<Guid, string>();

        var all = await _repository.GetAllNotificationTargetEmailsAsync(cancellationToken);
        return all
            .Where(kv => userIds.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    public Task<IReadOnlyList<Guid>> SearchUserIdsByVerifiedEmailAsync(
        string searchTerm, CancellationToken cancellationToken = default)
        => _repository.SearchUserIdsByVerifiedEmailAsync(searchTerm, cancellationToken);

    public Task<Guid?> GetOtherUserIdHavingEmailAsync(
        string email, Guid excludeUserId, CancellationToken cancellationToken = default)
        => _repository.GetOtherUserIdHavingEmailAsync(email, excludeUserId, cancellationToken);

    public Task<bool> IsEmailLinkedToAnyUserAsync(
        string email, CancellationToken cancellationToken = default) =>
        _repository.AnyWithEmailAsync(email, cancellationToken);

    public async Task<IReadOnlyList<UserEmailMatch>> MatchByEmailsAsync(
        IReadOnlyCollection<string> emails, CancellationToken cancellationToken = default)
    {
        var rows = await _repository.GetByEmailsAsync(emails, cancellationToken);
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

    /// <summary>
    /// Maintains the "exactly one IsPrimary=true verified row per user" invariant
    /// (Profiles.md Data Model — UserEmail.IsNotificationTarget). Centralizes the
    /// rule in one place so any code path that adds or removes a UserEmail row
    /// can call it as a safety step.
    ///
    /// Rules:
    /// - 0 verified rows → no-op (no candidate to promote; unverified rows are
    ///   never the notification target).
    /// - 1+ verified rows but 0 IsPrimary → pick a successor and promote it.
    /// - 2+ IsPrimary rows → demote all but the winner.
    ///
    /// Successor priority (highest to lowest):
    /// 1. Verified @nobodies.team row (Workspace identity wins per Peter's
    ///    feedback — the Workspace row IS the canonical Primary).
    /// 2. Most recently updated verified row.
    /// 3. Any verified row.
    /// </summary>
    private async Task EnsurePrimaryInvariantAsync(
        Guid userId, CancellationToken cancellationToken)
    {
        var emails = (await _repository.GetByUserIdForMutationAsync(userId, cancellationToken)).ToList();
        var verified = emails.Where(e => e.IsVerified).ToList();
        if (verified.Count == 0)
            return;

        var currentPrimaries = verified.Where(e => e.IsPrimary).ToList();

        // Pick the canonical winner from the verified set.
        // 1. Workspace (@nobodies.team) row wins per Peter's feedback — that row IS
        //    the canonical Primary when present.
        // 2. Otherwise prefer an existing IsPrimary row if any (stable — don't
        //    flip the primary just because a row was added/removed elsewhere).
        // 3. Otherwise most-recently-updated, with Id as the stable tiebreaker.
        var winner =
            verified.FirstOrDefault(e => e.Email.EndsWith("@nobodies.team", StringComparison.OrdinalIgnoreCase))
            ?? currentPrimaries.OrderBy(e => e.Id).FirstOrDefault()
            ?? verified.OrderByDescending(e => e.UpdatedAt).ThenBy(e => e.Id).First();

        // Already correct: exactly one primary, and it's the winner.
        if (currentPrimaries.Count == 1 && currentPrimaries[0].Id == winner.Id)
            return;

        var now = _clock.GetCurrentInstant();
        var changed = new List<UserEmail>();
        foreach (var row in verified)
        {
            var shouldBePrimary = row.Id == winner.Id;
            if (row.IsPrimary != shouldBePrimary)
            {
                row.IsPrimary = shouldBePrimary;
                row.UpdatedAt = now;
                changed.Add(row);
            }
        }

        if (changed.Count == 0)
            return;

        await _repository.UpdateBatchAsync(changed, cancellationToken);
        await _userInfoInvalidator.InvalidateAsync(userId, cancellationToken);
    }

    /// <summary>
    /// Issue nobodies-collective/Humans#687: maintains the "at most one
    /// IsGoogle=true verified row per user" invariant. Mirrors
    /// <see cref="EnsurePrimaryInvariantAsync"/> — when ≥1 verified row exists
    /// for the user but none is IsGoogle, picks the canonical winner and stamps
    /// it. Single source of truth for the Google identity, replacing the
    /// drift-prone <c>User.GoogleEmail</c> shadow column.
    ///
    /// Rules:
    /// - 0 verified rows → no-op (no candidate to promote).
    /// - 1+ verified rows but 0 IsGoogle → pick a winner and stamp it.
    /// - 2+ IsGoogle rows → demote all but the winner.
    /// - Exactly 1 IsGoogle row that is verified → no-op (stable).
    ///
    /// Successor priority (highest to lowest), same as
    /// <see cref="EnsurePrimaryInvariantAsync"/>:
    /// 1. Verified @nobodies.team row (Workspace identity wins).
    /// 2. Existing IsGoogle row (stable — don't flip the Google identity just
    ///    because a sibling row changed).
    /// 3. Most-recently-updated verified row, with Id as stable tiebreaker.
    /// </summary>
    private async Task EnsureGoogleInvariantAsync(
        Guid userId, CancellationToken cancellationToken)
    {
        var emails = (await _repository.GetByUserIdForMutationAsync(userId, cancellationToken)).ToList();
        var verified = emails.Where(e => e.IsVerified).ToList();
        if (verified.Count == 0)
            return;

        var currentGoogles = verified.Where(e => e.IsGoogle).ToList();

        // Pick the canonical winner from the verified set — same precedence as
        // EnsurePrimaryInvariantAsync.
        var winner =
            verified.FirstOrDefault(e => e.Email.EndsWith("@nobodies.team", StringComparison.OrdinalIgnoreCase))
            ?? currentGoogles.OrderBy(e => e.Id).FirstOrDefault()
            ?? verified.OrderByDescending(e => e.UpdatedAt).ThenBy(e => e.Id).First();

        // Already correct: exactly one IsGoogle row, and it's the winner.
        if (currentGoogles.Count == 1 && currentGoogles[0].Id == winner.Id)
            return;

        var now = _clock.GetCurrentInstant();
        var changed = new List<UserEmail>();
        foreach (var row in verified)
        {
            var shouldBeGoogle = row.Id == winner.Id;
            if (row.IsGoogle != shouldBeGoogle)
            {
                row.IsGoogle = shouldBeGoogle;
                row.UpdatedAt = now;
                changed.Add(row);
            }
        }

        if (changed.Count == 0)
            return;

        await _repository.UpdateBatchAsync(changed, cancellationToken);
        await _userInfoInvalidator.InvalidateAsync(userId, cancellationToken);
    }

    /// <summary>
    /// Issue nobodies-collective/Humans#687: single orchestrator for every
    /// UserEmail-add path. Adds the row, then runs the Primary and Google
    /// invariants and invalidates the UserInfo cache. The four call sites
    /// (<see cref="AddEmailAsync"/>, <see cref="AddVerifiedEmailAsync"/>,
    /// <see cref="LinkAsync"/>, <see cref="AddProvisionedEmailAsync"/>) all
    /// route through here so no path can silently skip an invariant.
    /// </summary>
    private async Task AddRowWithInvariantsAsync(
        UserEmail row, CancellationToken cancellationToken)
    {
        await _repository.AddAsync(row, cancellationToken);
        await EnsurePrimaryInvariantAsync(row.UserId, cancellationToken);
        await EnsureGoogleInvariantAsync(row.UserId, cancellationToken);
        await _userInfoInvalidator.InvalidateAsync(row.UserId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task AddProvisionedEmailAsync(
        Guid userId, string email, CancellationToken cancellationToken = default)
    {
        var normalizedEmail = EmailNormalization.NormalizeForComparison(email);
        var alternateEmail = GetAlternateComparableEmail(normalizedEmail);

        // Idempotent — duplicate provisioning attempts (e.g. retried imports)
        // must not re-add the row.
        if (await _repository.ExistsForUserAsync(userId, normalizedEmail, alternateEmail, cancellationToken))
            return;

        var now = _clock.GetCurrentInstant();
        var row = new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Email = email,
            IsVerified = true,
            // IsPrimary / IsGoogle set by the orchestrator's invariants.
            IsPrimary = false,
            Visibility = null,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await AddRowWithInvariantsAsync(row, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Guid?> FindAnyUserIdByEmailAsync(
        string email, CancellationToken cancellationToken = default)
    {
        var normalizedEmail = EmailNormalization.NormalizeForComparison(email);
        var alternateEmail = GetAlternateComparableEmail(normalizedEmail);
        var match = await _repository.FindByNormalizedEmailAsync(
            normalizedEmail, alternateEmail, cancellationToken);
        return match?.UserId;
    }

    /// <inheritdoc />
    public async Task<(Guid UserId, Guid EmailId)?> FindAnyEmailRowByAddressAsync(
        string email, CancellationToken cancellationToken = default)
    {
        var normalizedEmail = EmailNormalization.NormalizeForComparison(email);
        var alternateEmail = GetAlternateComparableEmail(normalizedEmail);
        var match = await _repository.FindByNormalizedEmailAsync(
            normalizedEmail, alternateEmail, cancellationToken);
        if (match is null) return null;
        return (match.UserId, match.Id);
    }

    /// <inheritdoc />
    public async Task<bool> SetGoogleAsync(
        Guid userId, Guid userEmailId, Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var row = await _repository.GetByIdAndUserIdAsync(userEmailId, userId, cancellationToken);
        if (row is null || !row.IsVerified) return false;

        // Capture the previous Google email (if any) for the audit description.
        // ReadOnly is fine — SetGoogleExclusiveAsync is the canonical mutation.
        var allEmails = await _repository.GetByUserIdReadOnlyAsync(userId, cancellationToken);
        var previousGoogle = allEmails.FirstOrDefault(e => e.IsGoogle && e.Id != row.Id);

        var now = _clock.GetCurrentInstant();
        await _repository.SetGoogleExclusiveAsync(userId, row.Id, now, cancellationToken);
        await _userInfoInvalidator.InvalidateAsync(userId, cancellationToken);

        var description = previousGoogle is null
            ? $"Set Google identity to {row.Email}"
            : $"Set Google identity to {row.Email} (was {previousGoogle.Email})";

        await _auditLogService.LogAsync(
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
        var row = await _repository.GetByIdAndUserIdAsync(userEmailId, userId, cancellationToken);
        if (row is null || !row.IsGoogle) return false;

        // Only allow clearing IsGoogle when the user has another row also
        // carrying the flag (the duplicate-flag recovery scenario). Clearing
        // the sole IsGoogle row would leave the user in the ZeroIsGoogle state
        // EmailProblems flags as a bug.
        var allEmails = await _repository.GetByUserIdReadOnlyAsync(userId, cancellationToken);
        if (allEmails.Count(e => e.IsGoogle) <= 1) return false;

        row.IsGoogle = false;
        row.UpdatedAt = _clock.GetCurrentInstant();
        await _repository.UpdateAsync(row, cancellationToken);
        await _userInfoInvalidator.InvalidateAsync(userId, cancellationToken);

        await _auditLogService.LogAsync(
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
        var row = await _repository.GetByIdAndUserIdAsync(userEmailId, userId, cancellationToken);
        if (row is null || !row.IsPrimary) return false;

        // Only allow clearing IsPrimary when the user has another verified
        // IsPrimary row (the duplicate-flag recovery scenario). The verified
        // filter mirrors the view's `hasMultiplePrimary` and the scanner's
        // notion of a primary (`verifiedPrimaryCount`); without it, a row
        // in `IsPrimary=true,IsVerified=false` would count as a successor
        // and clearing the verified primary would leave zero verified
        // primaries — the ZeroIsPrimary state EmailProblems flags as a bug.
        var allEmails = await _repository.GetByUserIdReadOnlyAsync(userId, cancellationToken);
        if (allEmails.Count(e => e.IsPrimary && e.IsVerified) <= 1) return false;

        row.IsPrimary = false;
        row.UpdatedAt = _clock.GetCurrentInstant();
        await _repository.UpdateAsync(row, cancellationToken);
        // Don't auto-promote a successor — the admin is resolving a
        // duplicate-IsPrimary state and may want to pick the new primary
        // deliberately.
        await _userInfoInvalidator.InvalidateAsync(userId, cancellationToken);

        await _auditLogService.LogAsync(
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
        var allEmails = await _repository.GetAllAsync(cancellationToken);

        var perUser = allEmails
            .GroupBy(e => e.UserId)
            .Select(g =>
            {
                var verified = g.Where(e => e.IsVerified).ToList();
                var isGoogleCount = g.Count(e => e.IsGoogle);
                var verifiedIsGoogleCount = verified.Count(e => e.IsGoogle);
                var verifiedPrimaryCount = verified.Count(e => e.IsPrimary);
                var hasMultipleGoogle = isGoogleCount > 1;
                // EnsureGoogleInvariantAsync only stamps IsGoogle on verified rows,
                // so the "zero" check must mirror that — an unverified IsGoogle row
                // is itself a violation, not a satisfier of the invariant.
                var hasZeroGoogle = verified.Count > 0 && verifiedIsGoogleCount == 0;
                var hasPrimaryProblem = verified.Count > 0 && verifiedPrimaryCount != 1;
                return (
                    UserId: g.Key,
                    IsGoogleCount: isGoogleCount,
                    VerifiedCount: verified.Count,
                    VerifiedPrimaryCount: verifiedPrimaryCount,
                    HasMultipleGoogle: hasMultipleGoogle,
                    HasZeroGoogle: hasZeroGoogle,
                    HasPrimaryProblem: hasPrimaryProblem);
            })
            .Where(x => x.HasMultipleGoogle || x.HasZeroGoogle || x.HasPrimaryProblem)
            .ToList();

        if (perUser.Count == 0)
            return [];

        var users = await _userService.GetUserInfosAsync(
            perUser.Select(x => x.UserId).ToList(),
            cancellationToken);

        return perUser
            .Select(x => new UserEmailFlagViolation(
                x.UserId,
                users.TryGetValue(x.UserId, out var user) ? user.DisplayName : null,
                x.IsGoogleCount,
                x.VerifiedCount,
                x.VerifiedPrimaryCount,
                x.HasMultipleGoogle,
                x.HasZeroGoogle,
                x.HasPrimaryProblem))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UserEmailOrphan>> GetOrphanUserEmailsAsync(CancellationToken ct = default)
    {
        var allEmails = await _repository.GetAllAsync(ct);
        var liveUserIds = (await _userService.GetAllUserInfosAsync(ct).ConfigureAwait(false))
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
        var row = await _repository.GetByIdReadOnlyAsync(emailId, ct);
        if (row is null) return false;

        var deleted = await _repository.RemoveByIdAsync(emailId, ct);
        if (deleted)
            await _userInfoInvalidator.InvalidateAsync(row.UserId, ct);
        return deleted;
    }

    /// <inheritdoc />
    public async Task<bool> UnlinkAsync(
        Guid userId, Guid userEmailId, Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var row = await _repository.GetByIdAndUserIdAsync(userEmailId, userId, cancellationToken);
        if (row is null) return false;
        if (string.IsNullOrEmpty(row.Provider) || string.IsNullOrEmpty(row.ProviderKey))
            return false;

        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is null) return false;

        // Capture provider/key/email before mutation — RemoveAsync may detach
        // the entity and the audit description needs them.
        var provider = row.Provider;
        var providerKey = row.ProviderKey;
        var email = row.Email;

        var removeLogin = await _userManager.RemoveLoginAsync(user, provider, providerKey);
        if (!removeLogin.Succeeded)
        {
            // Hard-fail: don't delete the UserEmail row if Identity refused to
            // remove the AspNetUserLogins row. Otherwise the user would believe
            // they unlinked their Google account (UserEmail row is gone) but
            // they could still sign in via Google (AspNetUserLogins row
            // persists). Hard-failing keeps the two stores in sync — the caller
            // can retry, and an admin can investigate the logged failure.
            _logger.LogError(
                "UnlinkAsync: UserManager.RemoveLoginAsync failed for user {UserId} provider {Provider}; aborting unlink to preserve consistency between AspNetUserLogins and user_emails. Errors: {Errors}",
                userId, provider,
                string.Join("; ", removeLogin.Errors.Select(e => $"{e.Code}:{e.Description}")));
            return false;
        }

        await _repository.RemoveAsync(row, cancellationToken);

        // If the unlinked row was primary, promote the highest-priority
        // remaining verified row.
        await EnsurePrimaryInvariantAsync(userId, cancellationToken);

        // If the unlinked row was the IsGoogle row, restamp the canonical
        // remaining @nobodies.team row so the user doesn't drift into the
        // zero-IsGoogle state.
        await EnsureGoogleInvariantAsync(userId, cancellationToken);

        await _userInfoInvalidator.InvalidateAsync(userId, cancellationToken);

        await _auditLogService.LogAsync(
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
        var now = _clock.GetCurrentInstant();
        var rows = (await _repository.GetByUserIdForMutationAsync(userId, cancellationToken)).ToList();

        var tagged = rows.FirstOrDefault(r =>
            string.Equals(r.Provider, provider, StringComparison.Ordinal) &&
            string.Equals(r.ProviderKey, providerKey, StringComparison.Ordinal));

        // 1. NoChange: tagged row already at claim email.
        if (tagged is not null &&
            string.Equals(tagged.Email, claimEmail, StringComparison.OrdinalIgnoreCase))
        {
            return new OAuthReconcileResult(
                ReconcileOutcome.NoChange, null, tagged.Id, null, null, null, false);
        }

        var siblingAtClaim = rows.FirstOrDefault(r =>
            !ReferenceEquals(r, tagged) &&
            string.Equals(r.Email, claimEmail, StringComparison.OrdinalIgnoreCase));

        // Every remaining branch (rewrite, tag-move, insert) ends with a
        // verified row at claimEmail. Cross-user check fires before any of
        // them mutate. Normalize the claim email through the same comparison
        // rules every other UserEmail lookup uses (gmail/googlemail alternate,
        // case-insensitive, trimmed) so the security-critical displacement
        // gate cannot be bypassed by a Gmail dot-alias.
        var normalizedClaim = EmailNormalization.NormalizeForComparison(claimEmail);
        var alternateClaim = GetAlternateComparableEmail(normalizedClaim);
        var blocker = await _repository.FindOtherUsersVerifiedRowAsync(
            normalizedClaim, alternateClaim, userId, cancellationToken);

        // 2. CrossUserBlocked: another user verified-holds the claim and the
        //    provider's claim is unverified → no mutation, audit the attempt.
        if (blocker is not null && !claimEmailVerified)
        {
            // CrossUserBlocked needs the displaced user's rows for the
            // diagnostic only — load them here on the rare blocked path.
            var blockerRows = await _repository.GetByUserIdForMutationAsync(
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

            _logger.LogError(
                "OAuth cross-user collision BLOCKED (unverified claim): {Description}",
                blockedDescription);

            await _auditLogService.LogAsync(
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

        // 3. Decide the cross-user displacement (snapshot only — does not
        //    mutate the DB yet). The displacement and the signing user's
        //    mutation commit together in step 5.
        var displaced = false;
        Guid? displacedUserId = null;
        Guid? displacedRowId = null;
        string? displacedEmail = null;
        var displacedUserLeftWithoutVerifiedEmail = false;
        string? collisionDiagnostic = null;

        if (blocker is not null && claimEmailVerified)
        {
            var displacedUsersRows = await _repository.GetByUserIdForMutationAsync(
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
            // Tag-move (explicit old-tagged row + sibling holds claim email).
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
            // Rewrite in place — tagged row holds a different email and no
            // sibling of the same user holds the claim email.
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
            // No tagged row but the user has an existing row at the claim
            // email — attach the tag (legacy backfill / first OAuth sign-in
            // after a plain-email account).
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
            // Fresh row — no tagged row and no sibling at the claim email.
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

        // 5. Apply the displacement + signing-user mutation atomically — one
        //    DbContext, one transaction. Either both happen or neither does;
        //    the displaced user's row cannot be deleted while the signing
        //    user's mutation is left undone.
        await _repository.ApplyReconcilePlanAsync(
            displacedRowToDelete: displaced ? blocker : null,
            rowToDelete: rowToDelete,
            rowToUpdate: rowToUpdate,
            rowToInsert: rowToInsert,
            cancellationToken);

        // 6. Invariants + cache invalidation on every affected user. These
        //    run AFTER the data-change commit. They are idempotent and the
        //    next sign-in re-runs them — when they can't run (process crash
        //    after step 5), no destructive state is left in the DB.
        if (displaced)
        {
            await EnsurePrimaryInvariantAsync(blocker!.UserId, cancellationToken);
            await EnsureGoogleInvariantAsync(blocker.UserId, cancellationToken);
            await _userInfoInvalidator.InvalidateAsync(blocker.UserId, cancellationToken);
        }
        await EnsurePrimaryInvariantAsync(userId, cancellationToken);
        await EnsureGoogleInvariantAsync(userId, cancellationToken);
        await _userInfoInvalidator.InvalidateAsync(userId, cancellationToken);

        // 7. Audit. The cross-user-displaced path writes the OAuthRename-
        //    Collision pair as the audit for this callback; we don't also
        //    write GoogleEmailRenamed / UserEmailLinked on top.
        if (displaced)
        {
            _logger.LogError(
                "OAuth cross-user displacement (verified claim): {Description}",
                collisionDiagnostic);

            await _auditLogService.LogAsync(
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
            await _auditLogService.LogAsync(
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

        await _auditLogService.LogAsync(
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

    /// <summary>
    /// Builds the structured diagnostic string used by every cross-user
    /// collision outcome (<see cref="ReconcileOutcome.CrossUserDisplaced"/>
    /// and <see cref="ReconcileOutcome.CrossUserBlocked"/>). Captures every
    /// available field so admins can trace what the provider claimed,
    /// what state both users were in, and which row was deleted or blocked.
    /// </summary>
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
        var loginsByUser = await _userService.GetExternalLoginsByUserIdsAsync([signingUserId, displaced.UserId], ct);
        var signingLogins = loginsByUser.TryGetValue(signingUserId, out var sl) ? sl : [];
        var displacedLogins = loginsByUser.TryGetValue(displaced.UserId, out var dl) ? dl : [];

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder();
        sb.Append(inv, $"OAuth cross-user {kind}: provider={provider} sub={providerKey} ");
        sb.Append(inv, $"claimEmail={claimEmail} claimEmailVerified={claimEmailVerified} ");
        sb.Append(inv, $"previousEmail={previousEmail ?? "(none)"} attemptedAt={_clock.GetCurrentInstant()}. ");
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
