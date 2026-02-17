using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Humans.Application.DTOs;
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

        return emails.Select(e => new UserEmailEditDto(
            e.Id,
            e.Email,
            e.IsVerified,
            e.IsOAuth,
            e.IsNotificationTarget,
            e.Visibility,
            IsPendingVerification: !e.IsVerified && e.VerificationSentAt.HasValue
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

    public async Task<string> AddEmailAsync(
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

        // Check uniqueness among verified emails (case-insensitive)
        var verifiedExists = await _dbContext.UserEmails
            .AnyAsync(e => e.IsVerified && EF.Functions.ILike(e.Email, email), cancellationToken);

        if (verifiedExists)
        {
            throw new ValidationException("This email address is already in use.");
        }

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

        return token;
    }

    public async Task<string> VerifyEmailAsync(
        Guid userId,
        string token,
        CancellationToken cancellationToken = default)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString())
            ?? throw new InvalidOperationException("User not found.");

        // Find the unverified email for this user
        var pendingEmail = await _dbContext.UserEmails
            .FirstOrDefaultAsync(e => e.UserId == userId && !e.IsVerified && !e.IsOAuth, cancellationToken);

        if (pendingEmail == null)
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

        // Re-check uniqueness (guard against race conditions)
        var emailInUse = await _dbContext.UserEmails
            .AnyAsync(e => e.Id != pendingEmail.Id
                && e.IsVerified
                && EF.Functions.ILike(e.Email, pendingEmail.Email), cancellationToken);

        if (emailInUse)
        {
            _dbContext.UserEmails.Remove(pendingEmail);
            await _dbContext.SaveChangesAsync(cancellationToken);
            throw new ValidationException("This email address has been claimed by another account.");
        }

        pendingEmail.IsVerified = true;
        pendingEmail.UpdatedAt = _clock.GetCurrentInstant();
        await _dbContext.SaveChangesAsync(cancellationToken);

        return pendingEmail.Email;
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

            if (oauthEmail != null)
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

    /// <summary>
    /// Returns the set of visibility levels a viewer with the given access level can see.
    /// Visibility is stored as string in DB, so >= comparison doesn't work correctly.
    /// </summary>
    private static List<ContactFieldVisibility> GetAllowedVisibilities(ContactFieldVisibility accessLevel) =>
        accessLevel switch
        {
            ContactFieldVisibility.BoardOnly => [ContactFieldVisibility.BoardOnly, ContactFieldVisibility.LeadsAndBoard, ContactFieldVisibility.MyTeams, ContactFieldVisibility.AllActiveProfiles],
            ContactFieldVisibility.LeadsAndBoard => [ContactFieldVisibility.LeadsAndBoard, ContactFieldVisibility.MyTeams, ContactFieldVisibility.AllActiveProfiles],
            ContactFieldVisibility.MyTeams => [ContactFieldVisibility.MyTeams, ContactFieldVisibility.AllActiveProfiles],
            ContactFieldVisibility.AllActiveProfiles => [ContactFieldVisibility.AllActiveProfiles],
            _ => [ContactFieldVisibility.AllActiveProfiles]
        };
}
