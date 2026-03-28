using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Humans.Application.DTOs;
using Humans.Domain.Helpers;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;

namespace Humans.Infrastructure.Services;

/// <summary>
/// Service for managing user email addresses.
/// </summary>
public class UserEmailService : IUserEmailService
{
    private readonly HumansDbContext _dbContext;
    private readonly UserManager<User> _userManager;
    private readonly IClock _clock;

    private const string EmailVerificationTokenPurpose = "UserEmailVerification";

    public UserEmailService(
        HumansDbContext dbContext,
        UserManager<User> userManager,
        IClock clock)
    {
        _dbContext = dbContext;
        _userManager = userManager;
        _clock = clock;
    }

    public async Task<IReadOnlyList<UserEmailEditDto>> GetUserEmailsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var emails = await _dbContext.UserEmails
            .AsNoTracking()
            .Where(e => e.UserId == userId)
            .OrderBy(e => e.DisplayOrder)
            .ThenBy(e => e.CreatedAt)
            .ToListAsync(cancellationToken);

        // Check which emails have pending merge requests
        var emailIds = emails.Select(e => e.Id).ToList();
        var mergePendingEmailIds = await _dbContext.AccountMergeRequests
            .Where(r => emailIds.Contains(r.PendingEmailId)
                && r.Status == AccountMergeRequestStatus.Pending)
            .Select(r => r.PendingEmailId)
            .ToListAsync(cancellationToken);
        var mergePendingSet = mergePendingEmailIds.ToHashSet();

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
        Guid userId,
        ContactFieldVisibility accessLevel,
        CancellationToken cancellationToken = default)
    {
        // Load verified emails from DB, then filter by visibility in memory.
        // Visibility is stored as string in DB, so enum comparisons don't translate correctly to SQL.
        var allowed = GetAllowedVisibilities(accessLevel);
        var emails = (await _dbContext.UserEmails
            .AsNoTracking()
            .Where(e => e.UserId == userId && e.IsVerified && e.Visibility != null)
            .OrderBy(e => e.DisplayOrder)
            .ThenBy(e => e.CreatedAt)
            .ToListAsync(cancellationToken))
            .Where(e => allowed.Contains(e.Visibility!.Value))
            .ToList();

        return emails.Select(e => new UserEmailDto(
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
        Guid userId,
        string email,
        CancellationToken cancellationToken = default)
    {
        email = email.Trim();

        // Validate email format
        if (!new EmailAddressAttribute().IsValid(email))
        {
            throw new ValidationException("Please enter a valid email address.");
        }

        // Check if this email already exists for this user
        var existingForUser = await _dbContext.UserEmails
            .AnyAsync(e => e.UserId == userId && EF.Functions.ILike(e.Email, email), cancellationToken);

        if (existingForUser)
        {
            throw new ValidationException("This email address is already in your account.");
        }

        // Check if there's already a pending merge request for this email from this user
        var pendingMerge = await _dbContext.AccountMergeRequests
            .AnyAsync(r => r.TargetUserId == userId
                && EF.Functions.ILike(r.Email, email)
                && r.Status == AccountMergeRequestStatus.Pending, cancellationToken);

        if (pendingMerge)
        {
            throw new ValidationException("A merge request is already pending for this email address.");
        }

        // Check uniqueness among verified emails (case-insensitive)
        // Instead of blocking, flag as conflict for merge flow
        var isConflict = await _dbContext.UserEmails
            .AnyAsync(e => e.UserId != userId && e.IsVerified && EF.Functions.ILike(e.Email, email), cancellationToken);

        var user = await _userManager.FindByIdAsync(userId.ToString())
            ?? throw new InvalidOperationException("User not found.");

        // Check if same as OAuth login email
        if (string.Equals(email, user.Email, StringComparison.OrdinalIgnoreCase))
        {
            throw new ValidationException("This is already your sign-in email.");
        }

        var now = _clock.GetCurrentInstant();

        // Get max display order
        var maxOrder = await _dbContext.UserEmails
            .Where(e => e.UserId == userId)
            .MaxAsync(e => (int?)e.DisplayOrder, cancellationToken) ?? -1;

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

        _dbContext.UserEmails.Add(userEmail);
        await _dbContext.SaveChangesAsync(cancellationToken);

        // Generate verification token
        var token = await _userManager.GenerateUserTokenAsync(
            user,
            TokenOptions.DefaultEmailProvider,
            $"{EmailVerificationTokenPurpose}:{userEmail.Id}");

        return new AddEmailResult(token, isConflict);
    }

    public async Task<VerifyEmailResult> VerifyEmailAsync(
        Guid userId,
        string token,
        CancellationToken cancellationToken = default)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString())
            ?? throw new InvalidOperationException("User not found.");

        // Find the unverified email for this user
        var pendingEmail = await _dbContext.UserEmails
            .FirstOrDefaultAsync(e => e.UserId == userId && !e.IsVerified && !e.IsOAuth, cancellationToken);

        if (pendingEmail is null)
        {
            throw new ValidationException("No email pending verification.");
        }

        // Verify the token
        var isValid = await _userManager.VerifyUserTokenAsync(
            user,
            TokenOptions.DefaultEmailProvider,
            $"{EmailVerificationTokenPurpose}:{pendingEmail.Id}",
            token);

        if (!isValid)
        {
            throw new ValidationException("The verification link has expired or is invalid.");
        }

        // Check if this email is verified on another account
        var conflictingEmail = await _dbContext.UserEmails
            .FirstOrDefaultAsync(e => e.Id != pendingEmail.Id
                && e.IsVerified
                && EF.Functions.ILike(e.Email, pendingEmail.Email), cancellationToken);

        if (conflictingEmail is not null)
        {
            // Check for existing pending merge request to avoid duplicates
            // (e.g. email client link prefetch, double-click)
            var existingRequest = await _dbContext.AccountMergeRequests
                .AnyAsync(r => r.PendingEmailId == pendingEmail.Id
                    && r.Status == AccountMergeRequestStatus.Pending, cancellationToken);

            if (!existingRequest)
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

                _dbContext.AccountMergeRequests.Add(mergeRequest);
                await _dbContext.SaveChangesAsync(cancellationToken);
            }

            return new VerifyEmailResult(pendingEmail.Email, MergeRequestCreated: true);
        }

        pendingEmail.IsVerified = true;
        pendingEmail.UpdatedAt = _clock.GetCurrentInstant();
        await _dbContext.SaveChangesAsync(cancellationToken);

        return new VerifyEmailResult(pendingEmail.Email, MergeRequestCreated: false);
    }

    public async Task SetNotificationTargetAsync(
        Guid userId,
        Guid emailId,
        CancellationToken cancellationToken = default)
    {
        var emails = await _dbContext.UserEmails
            .Where(e => e.UserId == userId)
            .ToListAsync(cancellationToken);

        var target = emails.FirstOrDefault(e => e.Id == emailId)
            ?? throw new InvalidOperationException("Email not found.");

        if (!target.IsVerified)
        {
            throw new ValidationException("Only verified emails can be the notification target.");
        }

        var now = _clock.GetCurrentInstant();
        foreach (var email in emails)
        {
            email.IsNotificationTarget = email.Id == emailId;
            email.UpdatedAt = now;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task SetVisibilityAsync(
        Guid userId,
        Guid emailId,
        ContactFieldVisibility? visibility,
        CancellationToken cancellationToken = default)
    {
        var email = await _dbContext.UserEmails
            .FirstOrDefaultAsync(e => e.Id == emailId && e.UserId == userId, cancellationToken)
            ?? throw new InvalidOperationException("Email not found.");

        email.Visibility = visibility;
        email.UpdatedAt = _clock.GetCurrentInstant();
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteEmailAsync(
        Guid userId,
        Guid emailId,
        CancellationToken cancellationToken = default)
    {
        var email = await _dbContext.UserEmails
            .FirstOrDefaultAsync(e => e.Id == emailId && e.UserId == userId, cancellationToken)
            ?? throw new InvalidOperationException("Email not found.");

        if (email.IsOAuth)
        {
            throw new ValidationException("The sign-in email cannot be deleted.");
        }

        // If this was the notification target, reassign to OAuth email
        if (email.IsNotificationTarget)
        {
            var oauthEmail = await _dbContext.UserEmails
                .FirstOrDefaultAsync(e => e.UserId == userId && e.IsOAuth, cancellationToken);

            if (oauthEmail is not null)
            {
                oauthEmail.IsNotificationTarget = true;
                oauthEmail.UpdatedAt = _clock.GetCurrentInstant();
            }
        }

        _dbContext.UserEmails.Remove(email);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task RemoveAllEmailsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var emails = await _dbContext.UserEmails
            .Where(e => e.UserId == userId)
            .ToListAsync(cancellationToken);

        _dbContext.UserEmails.RemoveRange(emails);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task AddOAuthEmailAsync(
        Guid userId,
        string email,
        CancellationToken cancellationToken = default)
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

        _dbContext.UserEmails.Add(userEmail);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task AddVerifiedEmailAsync(
        Guid userId,
        string email,
        CancellationToken cancellationToken = default)
    {
        // Skip if email already exists for this user
        var exists = await _dbContext.UserEmails
            .AnyAsync(ue => ue.UserId == userId
                && EF.Functions.ILike(ue.Email, email), cancellationToken);
        if (exists)
            return;

        var now = _clock.GetCurrentInstant();
        var isNobodiesTeam = email.EndsWith("@nobodies.team", StringComparison.OrdinalIgnoreCase);

        // If @nobodies.team, clear existing notification target
        if (isNobodiesTeam)
        {
            var currentTarget = await _dbContext.UserEmails
                .FirstOrDefaultAsync(ue => ue.UserId == userId && ue.IsNotificationTarget, cancellationToken);
            if (currentTarget is not null)
            {
                currentTarget.IsNotificationTarget = false;
                currentTarget.UpdatedAt = now;
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

        _dbContext.UserEmails.Add(userEmail);

        // Auto-set GoogleEmail when a @nobodies.team email is added
        if (isNobodiesTeam)
        {
            var user = await _dbContext.Users.FindAsync([userId], cancellationToken);
            if (user is not null && user.GoogleEmail is null)
            {
                user.GoogleEmail = email;
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Returns the set of visibility levels a viewer with the given access level can see.
    /// Visibility is stored as string in DB, so >= comparison doesn't work correctly.
    /// </summary>
    private static List<ContactFieldVisibility> GetAllowedVisibilities(ContactFieldVisibility accessLevel) =>
        accessLevel switch
        {
            ContactFieldVisibility.BoardOnly => [ContactFieldVisibility.BoardOnly, ContactFieldVisibility.CoordinatorsAndBoard, ContactFieldVisibility.MyTeams, ContactFieldVisibility.AllActiveProfiles],
            ContactFieldVisibility.CoordinatorsAndBoard => [ContactFieldVisibility.CoordinatorsAndBoard, ContactFieldVisibility.MyTeams, ContactFieldVisibility.AllActiveProfiles],
            ContactFieldVisibility.MyTeams => [ContactFieldVisibility.MyTeams, ContactFieldVisibility.AllActiveProfiles],
            _ => [ContactFieldVisibility.AllActiveProfiles]
        };
}
