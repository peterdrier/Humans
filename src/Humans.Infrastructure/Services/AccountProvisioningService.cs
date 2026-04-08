using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Domain.Helpers;
using Humans.Infrastructure.Data;

namespace Humans.Infrastructure.Services;

/// <summary>
/// Idempotent account provisioning for import jobs.
/// Looks up existing users by email across all UserEmail records using normalizing comparison,
/// creates User + UserEmail when no match exists.
/// </summary>
public class AccountProvisioningService : IAccountProvisioningService
{
    private readonly HumansDbContext _dbContext;
    private readonly UserManager<User> _userManager;
    private readonly IAuditLogService _auditLogService;
    private readonly IClock _clock;
    private readonly ILogger<AccountProvisioningService> _logger;

    public AccountProvisioningService(
        HumansDbContext dbContext,
        UserManager<User> userManager,
        IAuditLogService auditLogService,
        IClock clock,
        ILogger<AccountProvisioningService> logger)
    {
        _dbContext = dbContext;
        _userManager = userManager;
        _auditLogService = auditLogService;
        _clock = clock;
        _logger = logger;
    }

    public async Task<AccountProvisioningResult> FindOrCreateUserByEmailAsync(
        string email, string? displayName, ContactSource source,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        // 1. Check UserEmail records (includes OAuth, verified, and unverified emails)
        var allUserEmails = await _dbContext.UserEmails
            .Include(ue => ue.User)
            .ToListAsync(ct);

        var matchingUserEmail = allUserEmails
            .FirstOrDefault(ue => NormalizingEmailComparer.Instance.Equals(ue.Email, email));

        if (matchingUserEmail is not null)
        {
            var existingUser = matchingUserEmail.User;
            _logger.LogDebug(
                "Found existing account {UserId} via UserEmail match for {Email} (source: {Source})",
                existingUser.Id, email, source);

            // Layer the ContactSource if the user was self-registered (no source yet)
            if (existingUser.ContactSource is null)
            {
                existingUser.ContactSource = source;
                await _dbContext.SaveChangesAsync(ct);
            }

            return new AccountProvisioningResult(existingUser, Created: false);
        }

        // 2. Also check User.Email directly (in case UserEmail record is missing)
        var allUsers = await _dbContext.Users.ToListAsync(ct);
        var matchingUser = allUsers
            .FirstOrDefault(u => u.Email is not null &&
                                 NormalizingEmailComparer.Instance.Equals(u.Email, email));

        if (matchingUser is not null)
        {
            _logger.LogDebug(
                "Found existing account {UserId} via User.Email match for {Email} (source: {Source})",
                matchingUser.Id, email, source);

            if (matchingUser.ContactSource is null)
            {
                matchingUser.ContactSource = source;
                await _dbContext.SaveChangesAsync(ct);
            }

            return new AccountProvisioningResult(matchingUser, Created: false);
        }

        // 3. No match found — create new User + UserEmail
        var resolvedDisplayName = string.IsNullOrWhiteSpace(displayName)
            ? email.Split('@')[0]
            : displayName;

        var now = _clock.GetCurrentInstant();

        var newUser = new User
        {
            UserName = email,
            Email = email,
            DisplayName = resolvedDisplayName,
            ContactSource = source,
            CreatedAt = now,
            EmailConfirmed = true,
        };

        var result = await _userManager.CreateAsync(newUser);
        if (!result.Succeeded)
        {
            var errors = string.Join(", ", result.Errors.Select(e => e.Description));
            throw new InvalidOperationException($"Failed to create account for {email}: {errors}");
        }

        var userEmail = new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = newUser.Id,
            Email = email,
            IsOAuth = false,
            IsVerified = true,
            IsNotificationTarget = true,
            Visibility = null,
            DisplayOrder = 0,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _dbContext.UserEmails.Add(userEmail);

        await _auditLogService.LogAsync(
            AuditAction.ContactCreated,
            nameof(User), newUser.Id,
            $"Account pre-created from {source} — {email}",
            nameof(AccountProvisioningService));

        await _dbContext.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Created new account {UserId} for {Email} (source: {Source}, displayName: {DisplayName})",
            newUser.Id, email, source, resolvedDisplayName);

        return new AccountProvisioningResult(newUser, Created: true);
    }
}
