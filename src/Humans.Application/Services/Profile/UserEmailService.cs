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

namespace Humans.Application.Services.Profile;

/// <summary>
/// Service for managing user email addresses. Business logic only —
/// no direct DbContext usage. Cross-section reads (AccountMergeRequests,
/// Users) are routed through their owning service interfaces.
/// </summary>
public sealed class UserEmailService : IUserEmailService
{
    private readonly IUserEmailRepository _repository;
    private readonly IUserService _userService;
    private readonly UserManager<User> _userManager;
    private readonly IClock _clock;
    private readonly IFullProfileInvalidator _fullProfileInvalidator;
    private readonly IAuditLogService _auditLogService;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<UserEmailService> _logger;

    private const string EmailVerificationTokenPurpose = "UserEmailVerification";

    public UserEmailService(
        IUserEmailRepository repository,
        IUserService userService,
        UserManager<User> userManager,
        IClock clock,
        IFullProfileInvalidator fullProfileInvalidator,
        IAuditLogService auditLogService,
        IServiceProvider serviceProvider,
        ILogger<UserEmailService> logger)
    {
        _repository = repository;
        _userService = userService;
        _userManager = userManager;
        _clock = clock;
        _fullProfileInvalidator = fullProfileInvalidator;
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

        await _repository.AddAsync(userEmail, cancellationToken);

        // Generate verification token via Identity
        var token = await _userManager.GenerateUserTokenAsync(
            user,
            TokenOptions.DefaultEmailProvider,
            $"{EmailVerificationTokenPurpose}:{userEmail.Id}");

        return new AddEmailResult(token, isConflict);
    }

    public async Task<VerifyEmailResult> VerifyEmailAsync(
        Guid userId, string token, CancellationToken cancellationToken = default)
    {
        var user = await _userService.GetByIdAsync(userId, cancellationToken)
            ?? throw new InvalidOperationException("User not found.");

        var userEmails = await _repository.GetByUserIdForMutationAsync(userId, cancellationToken);
        // TODO(nobodies-collective#611): VerifyEmailAsync uses FirstOrDefault on
        // (!IsVerified && Provider == null), which is ambiguous when multiple pending
        // plain rows exist for the same user. The token IS bound to a specific row Id
        // (purpose = "{EmailVerificationTokenPurpose}:{pendingEmail.Id}"), so a row
        // mismatch causes token validation to fail against the wrong row, surfacing as
        // a confusing user error even when the token is valid for ANOTHER pending row.
        // Surfaced during PR 4 review (peterdrier/Humans#376) — AdminAddEmail amplifies
        // the multi-pending-row scenario. Fix: load the row by the Id embedded in the
        // token's purpose suffix, not via FirstOrDefault.
        var pendingEmail = userEmails.FirstOrDefault(e => !e.IsVerified && e.Provider == null)
            ?? throw new ValidationException("No email pending verification.");

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

        await TryBackfillGoogleEmailAsync(userId, cancellationToken);
        await _fullProfileInvalidator.InvalidateAsync(userId, cancellationToken);

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

        // FullProfile.NotificationEmail derives from the row with IsPrimary=true.
        await _fullProfileInvalidator.InvalidateAsync(userId, cancellationToken);
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
        await _fullProfileInvalidator.InvalidateAsync(userId, cancellationToken);
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
        // sign in but cannot receive system notifications (GetEffectiveEmail falls
        // back to User.Email, which is null post email-decoupling), so blocking the
        // last-verified-row delete is preferable to silently dropping notifications.
        // Unverified rows aren't notification targets, so deleting one is safe.
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

        // FullProfile.NotificationEmail derives from user_emails; drop the stale entry so
        // admin/search/profile surfaces stop showing the removed address.
        await _fullProfileInvalidator.InvalidateAsync(userId, cancellationToken);

        return true;
    }

    public Task RemoveAllEmailsAsync(
        Guid userId, CancellationToken cancellationToken = default) =>
        _repository.RemoveAllForUserAsync(userId, cancellationToken);

    public async Task AddVerifiedEmailAsync(
        Guid userId, string email, CancellationToken cancellationToken = default)
    {
        var normalizedEmail = EmailNormalization.NormalizeForComparison(email);
        var alternateEmail = GetAlternateComparableEmail(normalizedEmail);

        if (await _repository.ExistsForUserAsync(userId, normalizedEmail, alternateEmail, cancellationToken))
            return;

        var now = _clock.GetCurrentInstant();
        var isNobodiesTeam = email.EndsWith("@nobodies.team", StringComparison.OrdinalIgnoreCase);

        var userEmail = new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Email = email,
            IsVerified = true,
            // IsPrimary set below by EnsurePrimaryInvariantAsync.
            IsPrimary = false,
            Visibility = ContactFieldVisibility.BoardOnly,
            CreatedAt = now,
            UpdatedAt = now
        };

        await _repository.AddAsync(userEmail, cancellationToken);

        await EnsurePrimaryInvariantAsync(userId, cancellationToken);

        // Auto-set GoogleEmail when @nobodies.team email is added (cross-section → IUserService)
        if (isNobodiesTeam)
        {
            await _userService.TrySetGoogleEmailAsync(userId, email, cancellationToken);
        }
    }

    public async Task<bool> TryBackfillGoogleEmailAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        // The legacy User.GoogleEmail column is checked by
        // IUserService.TrySetGoogleEmailAsync (which is a no-op when the
        // column is non-null), so no read-then-set race exists here.
        var allNobodies = await _repository.GetAllVerifiedNobodiesTeamEmailsAsync(cancellationToken);
        var nobodiesEmail = allNobodies.FirstOrDefault(e => e.UserId == userId)?.Email;
        if (nobodiesEmail is null)
            return false;

        return await _userService.TrySetGoogleEmailAsync(userId, nobodiesEmail, cancellationToken);
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
            var users = await _userService.GetByIdsAsync(missing, cancellationToken);
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

    public Task<Guid?> GetUserIdByVerifiedEmailAsync(
        string email, CancellationToken cancellationToken = default) =>
        _repository.GetUserIdByVerifiedEmailAsync(email, cancellationToken);

    public async Task<IReadOnlyList<string>> GetVerifiedEmailsForUserAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var emails = await _repository.GetByUserIdReadOnlyAsync(userId, cancellationToken);
        return emails
            .Where(e => e.IsVerified)
            .Select(e => e.Email)
            .ToList();
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

    public async Task RewriteEmailAddressAsync(
        Guid userId, string oldEmail, string newEmail,
        CancellationToken cancellationToken = default)
    {
        await _repository.RewriteEmailAddressAsync(
            userId, oldEmail, newEmail, _clock.GetCurrentInstant(), cancellationToken);
    }

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
        await _fullProfileInvalidator.InvalidateAsync(userId, cancellationToken);
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
        await _fullProfileInvalidator.InvalidateAsync(userId, cancellationToken);

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
    public async Task<bool> LinkAsync(
        Guid userId, string provider, string providerKey, string email, Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.GetCurrentInstant();
        var existing = await _repository.GetByUserIdReadOnlyAsync(userId, cancellationToken);
        var match = existing.FirstOrDefault(
            e => string.Equals(e.Email, email, StringComparison.OrdinalIgnoreCase));

        Guid rowId;
        string description;

        if (match is not null)
        {
            // Re-fetch tracked entity for mutation. GetByIdAndUserIdAsync returns
            // a tracked row (per repo XML: "tracked for modification").
            var tracked = await _repository.GetByIdAndUserIdAsync(match.Id, userId, cancellationToken)
                ?? throw new InvalidOperationException(
                    $"UserEmail row {match.Id} disappeared between read and mutate.");

            // Successful OAuth proves ownership, so promote a previously-pending
            // plain row to verified. Without this the row is stranded —
            // VerifyEmailAsync filters on (Provider == null) and won't pick it up.
            var wasPending = !tracked.IsVerified;
            tracked.Provider = provider;
            tracked.ProviderKey = providerKey;
            tracked.IsVerified = true;
            tracked.UpdatedAt = now;
            await _repository.UpdateAsync(tracked, cancellationToken);

            rowId = tracked.Id;
            description = wasPending
                ? $"Linked {provider} `{tracked.Email}` to user (verified via OAuth)"
                : $"Linked {provider} `{tracked.Email}` to user";
        }
        else
        {
            var fresh = new UserEmail
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Email = email,
                IsVerified = true,
                IsPrimary = false,
                IsGoogle = false,
                Provider = provider,
                ProviderKey = providerKey,
                CreatedAt = now,
                UpdatedAt = now,
            };
            await _repository.AddAsync(fresh, cancellationToken);

            rowId = fresh.Id;
            description = $"Linked {provider} `{fresh.Email}` to user (new row)";
        }

        // First OAuth sign-in: promote the just-added row to primary so
        // GetEffectiveEmail() / NotificationEmail derivation has a target.
        await EnsurePrimaryInvariantAsync(userId, cancellationToken);

        await _fullProfileInvalidator.InvalidateAsync(userId, cancellationToken);

        await _auditLogService.LogAsync(
            AuditAction.UserEmailLinked,
            nameof(User), userId,
            description,
            actorUserId,
            relatedEntityId: rowId, relatedEntityType: nameof(UserEmail));

        return true;
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

        await _fullProfileInvalidator.InvalidateAsync(userId, cancellationToken);

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
    public async Task<UserEmailProviderMatch?> FindByProviderKeyAsync(
        string provider, string providerKey,
        CancellationToken cancellationToken = default)
    {
        var matches = await _repository.FindAllByProviderKeyAsync(
            provider, providerKey, cancellationToken);
        if (matches.Count > 1)
        {
            _logger.LogWarning(
                "FindByProviderKeyAsync: multiple rows matched provider={Provider} providerKey={ProviderKey} — single-row-per-pair invariant violated; returning first match {EmailId}.",
                provider, providerKey, matches[0].Id);
        }
        if (matches.Count == 0) return null;
        var first = matches[0];
        return new UserEmailProviderMatch(first.Id, first.UserId, first.Email);
    }
}
