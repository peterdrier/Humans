using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Domain.Helpers;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Application.Services.Users;

/// <summary>
/// Idempotent account provisioning for import jobs (ticket import, MailerLite
/// import). Looks up existing users by email across all <see cref="UserEmail"/>
/// records and creates a new User + UserEmail when no match exists.
/// </summary>
/// <remarks>
/// Application-layer implementation — goes through <see cref="IUserRepository"/>
/// and <see cref="IUserEmailRepository"/>, never injects <c>DbContext</c>.
/// <para>
/// User creation itself still goes through <see cref="UserManager{TUser}"/>
/// because ASP.NET Identity owns the password hashing / concurrency stamp /
/// security stamp fields. UserManager is a framework abstraction and is
/// allowed in the Application layer (see design-rules §2a exception).
/// </para>
/// </remarks>
public sealed class AccountProvisioningService : IAccountProvisioningService
{
    private readonly IUserRepository _userRepository;
    private readonly IUserEmailRepository _userEmailRepository;
    private readonly UserManager<User> _userManager;
    private readonly IAuditLogService _auditLogService;
    private readonly IClock _clock;
    private readonly ILogger<AccountProvisioningService> _logger;

    public AccountProvisioningService(
        IUserRepository userRepository,
        IUserEmailRepository userEmailRepository,
        UserManager<User> userManager,
        IAuditLogService auditLogService,
        IClock clock,
        ILogger<AccountProvisioningService> logger)
    {
        _userRepository = userRepository;
        _userEmailRepository = userEmailRepository;
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

        var normalizedEmail = EmailNormalization.NormalizeForComparison(email);
        var alternateEmail = GetAlternateEmail(normalizedEmail);

        // 1. Check UserEmail records (includes OAuth, verified, and unverified emails)
        var matchingUserEmail = await _userEmailRepository.FindByNormalizedEmailAsync(
            normalizedEmail, alternateEmail, ct);

        if (matchingUserEmail is not null)
        {
            var existingUser = await _userRepository.GetByIdAsync(matchingUserEmail.UserId, ct);
            if (existingUser is null)
            {
                _logger.LogWarning(
                    "Orphan UserEmail {UserEmailId} references missing user {UserId} during lookup for {Email}",
                    matchingUserEmail.Id, matchingUserEmail.UserId, email);
            }
            else
            {
                _logger.LogDebug(
                    "Found existing account {UserId} via UserEmail match for {Email} (source: {Source})",
                    existingUser.Id, email, source);

                // Layer the ContactSource if the user was self-registered (no source yet)
                if (existingUser.ContactSource is null)
                {
                    await _userRepository.SetContactSourceIfNullAsync(existingUser.Id, source, ct);
                    existingUser.ContactSource = source;
                }

                return new AccountProvisioningResult(existingUser, Created: false);
            }
        }

        // 2. Also check User.Email directly (in case UserEmail record is missing)
        var matchingUser = await _userRepository.GetByEmailOrAlternateAsync(
            normalizedEmail, alternateEmail, ct);

        if (matchingUser is not null)
        {
            _logger.LogDebug(
                "Found existing account {UserId} via User.Email match for {Email} (source: {Source})",
                matchingUser.Id, email, source);

            if (matchingUser.ContactSource is null)
            {
                await _userRepository.SetContactSourceIfNullAsync(matchingUser.Id, source, ct);
                matchingUser.ContactSource = source;
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

        // UserManager persists the user (its Identity store calls SaveChanges).
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
        // AddAsync persists the UserEmail in its own context. The business save
        // (User + UserEmail) is complete before the audit-after-save log call.
        await _userEmailRepository.AddAsync(userEmail, ct);

        await _auditLogService.LogAsync(
            AuditAction.ContactCreated,
            nameof(User), newUser.Id,
            $"Account pre-created from {source} — {email}",
            nameof(AccountProvisioningService));

        _logger.LogInformation(
            "Created new account {UserId} for {Email} (source: {Source}, displayName: {DisplayName})",
            newUser.Id, email, source, resolvedDisplayName);

        return new AccountProvisioningResult(newUser, Created: true);
    }

    private static string? GetAlternateEmail(string normalizedEmail)
    {
        if (normalizedEmail.EndsWith("@gmail.com", StringComparison.Ordinal))
            return $"{normalizedEmail[..^"@gmail.com".Length]}@googlemail.com";

        return null;
    }
}
