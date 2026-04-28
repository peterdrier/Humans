using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Humans.Application.DTOs;
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
    private readonly IServiceProvider _serviceProvider;

    private const string EmailVerificationTokenPurpose = "UserEmailVerification";

    public UserEmailService(
        IUserEmailRepository repository,
        IUserService userService,
        UserManager<User> userManager,
        IClock clock,
        IFullProfileInvalidator fullProfileInvalidator,
        IServiceProvider serviceProvider)
    {
        _repository = repository;
        _userService = userService;
        _userManager = userManager;
        _clock = clock;
        _fullProfileInvalidator = fullProfileInvalidator;
        _serviceProvider = serviceProvider;
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
            e.IsOAuth,
            e.IsNotificationTarget,
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

        return visible.Select(e => new UserEmailDto(
            e.Id,
            e.Email,
            e.IsVerified,
            e.IsOAuth,
            e.IsNotificationTarget,
            e.Visibility,
            e.DisplayOrder
        )).ToList();
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
        var maxOrder = await _repository.GetMaxDisplayOrderAsync(userId, cancellationToken);

        var userEmail = new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Email = email,
            IsVerified = false,
            IsOAuth = false,
            IsNotificationTarget = false,
            DisplayOrder = maxOrder + 1,
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
        var pendingEmail = userEmails.FirstOrDefault(e => !e.IsVerified && !e.IsOAuth)
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

        return new VerifyEmailResult(pendingEmail.Email, MergeRequestCreated: false);
    }

    public async Task SetNotificationTargetAsync(
        Guid userId, Guid emailId, CancellationToken cancellationToken = default)
    {
        var emails = await _repository.GetByUserIdForMutationAsync(userId, cancellationToken);

        var target = emails.FirstOrDefault(e => e.Id == emailId)
            ?? throw new InvalidOperationException("Email not found.");

        if (!target.IsVerified)
            throw new ValidationException("Only verified emails can be the notification target.");

        var now = _clock.GetCurrentInstant();
        foreach (var email in emails)
        {
            email.IsNotificationTarget = email.Id == emailId;
            email.UpdatedAt = now;
        }

        await _repository.UpdateBatchAsync(emails.ToList(), cancellationToken);

        // FullProfile.NotificationEmail derives from the row with IsNotificationTarget=true.
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
    }

    public async Task DeleteEmailAsync(
        Guid userId, Guid emailId, CancellationToken cancellationToken = default)
    {
        var email = await _repository.GetByIdAndUserIdAsync(emailId, userId, cancellationToken)
            ?? throw new InvalidOperationException("Email not found.");

        // Preserve-at-least-one-verified-email invariant — replaces the old
        // IsOAuth-based block per email-identity-decoupling spec PR 1
        // (docs/superpowers/specs/2026-04-27-email-and-oauth-decoupling-design.md).
        //
        // The original rule was "preserve at least one auth method" (verified
        // UserEmail OR AspNetUserLogins row). Tightened to "preserve at least
        // one verified UserEmail" because OAuth-only users are still un-
        // notifiable: GetEffectiveEmail() falls back to User.Email which is
        // null for post-PR-1 users, so an OAuth-only state would silently
        // drop every system email (re-consent reminders, suspension notices,
        // password resets, etc.). The OAuth login still works for sign-in,
        // but signing in to an account that can't receive notifications is
        // worse UX than blocking the delete.
        //
        // We only enforce when removing a verified row — unverified rows
        // aren't notification targets and aren't usable for magic-link
        // sign-in, so deleting one cannot reduce the verified-email count.
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

            // If this row is the notification target, hand off to the next
            // verified row by display order so the user keeps a usable
            // notification address. The earlier "prefer IsOAuth" successor
            // ranking is intentionally dropped: AddOAuthEmailAsync sets
            // IsOAuth=true for magic-link signups too (pre-existing misnomer
            // tracked for PR 3 when the flag becomes Provider/ProviderKey),
            // so leaning on it here would mis-pick successors for users
            // who never signed up via OAuth.
            if (email.IsNotificationTarget)
            {
                var successor = allEmails
                    .Where(e => e.Id != emailId && e.IsVerified)
                    .OrderBy(e => e.DisplayOrder)
                    .FirstOrDefault();
                if (successor is not null)
                {
                    successor.IsNotificationTarget = true;
                    successor.UpdatedAt = _clock.GetCurrentInstant();
                    await _repository.UpdateAsync(successor, cancellationToken);
                }
            }
        }

        await _repository.RemoveAsync(email, cancellationToken);

        // FullProfile.NotificationEmail derives from user_emails; drop the stale entry so
        // admin/search/profile surfaces stop showing the removed address.
        await _fullProfileInvalidator.InvalidateAsync(userId, cancellationToken);
    }

    public Task RemoveAllEmailsAsync(
        Guid userId, CancellationToken cancellationToken = default) =>
        _repository.RemoveAllForUserAsync(userId, cancellationToken);

    public async Task AddOAuthEmailAsync(
        Guid userId, string email, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetCurrentInstant();

        var userEmail = new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Email = email,
            IsOAuth = true,
            IsVerified = true,
            IsNotificationTarget = true,
            Visibility = ContactFieldVisibility.BoardOnly,
            DisplayOrder = 0,
            CreatedAt = now,
            UpdatedAt = now
        };

        await _repository.AddAsync(userEmail, cancellationToken);
    }

    public async Task AddVerifiedEmailAsync(
        Guid userId, string email, CancellationToken cancellationToken = default)
    {
        var normalizedEmail = EmailNormalization.NormalizeForComparison(email);
        var alternateEmail = GetAlternateComparableEmail(normalizedEmail);

        if (await _repository.ExistsForUserAsync(userId, normalizedEmail, alternateEmail, cancellationToken))
            return;

        var now = _clock.GetCurrentInstant();
        var isNobodiesTeam = email.EndsWith("@nobodies.team", StringComparison.OrdinalIgnoreCase);

        // If @nobodies.team, clear existing notification target
        if (isNobodiesTeam)
        {
            var emails = await _repository.GetByUserIdForMutationAsync(userId, cancellationToken);
            var currentTarget = emails.FirstOrDefault(e => e.IsNotificationTarget);
            if (currentTarget is not null)
            {
                currentTarget.IsNotificationTarget = false;
                currentTarget.UpdatedAt = now;
                await _repository.UpdateAsync(currentTarget, cancellationToken);
            }
        }

        var userEmail = new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Email = email,
            IsOAuth = false,
            IsVerified = true,
            IsNotificationTarget = isNobodiesTeam,
            Visibility = ContactFieldVisibility.BoardOnly,
            DisplayOrder = 0,
            CreatedAt = now,
            UpdatedAt = now
        };

        await _repository.AddAsync(userEmail, cancellationToken);

        // Auto-set GoogleEmail when @nobodies.team email is added (cross-section → IUserService)
        if (isNobodiesTeam)
        {
            await _userService.TrySetGoogleEmailAsync(userId, email, cancellationToken);
        }
    }

    public async Task<bool> TryBackfillGoogleEmailAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        // Check if user already has a GoogleEmail (cross-section → IUserService)
        var user = await _userService.GetByIdAsync(userId, cancellationToken);
        if (user?.GoogleEmail is not null)
            return false;

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
                g => g.Any(e => e.IsNotificationTarget));
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
                g => g.OrderByDescending(e => e.IsNotificationTarget)
                    .ThenBy(e => e.CreatedAt)
                    .First().Email);
    }

    public async Task<IReadOnlyDictionary<Guid, string>> GetNotificationTargetEmailsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default)
    {
        if (userIds.Count == 0)
            return new Dictionary<Guid, string>();

        // Start with users who have a verified IsNotificationTarget row.
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
                r.Email, r.UserId, r.IsNotificationTarget, r.IsVerified, r.UpdatedAt))
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
}
