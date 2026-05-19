using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Application.Services.Users;

// Idempotent account provisioning for import jobs (ticket import, MailerLite). UserManager allowed per §2a exception.
public sealed class AccountProvisioningService(
    IUserRepository userRepository,
    IUserEmailService userEmailService,
    IProfileService profileService,
    UserManager<User> userManager,
    IAuditLogService auditLogService,
    IClock clock,
    ILogger<AccountProvisioningService> logger) : IAccountProvisioningService
{
    public async Task<AccountProvisioningResult> FindOrCreateUserByEmailAsync(
        string email, string? displayName, ContactSource source,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        // 1. Look up across OAuth / verified / unverified — via service so orchestrator owns invariants (see #687).
        var matchingUserId = await userEmailService.FindAnyUserIdByEmailAsync(email, ct);

        if (matchingUserId is not null)
        {
            var existingUser = await userRepository.GetByIdAsync(matchingUserId.Value, ct);
            if (existingUser is null)
            {
                logger.LogWarning(
                    "Orphan UserEmail references missing user {UserId} during lookup for {Email}",
                    matchingUserId.Value, email);
            }
            else
            {
                logger.LogDebug(
                    "Found existing account {UserId} via UserEmail match for {Email} (source: {Source})",
                    existingUser.Id, email, source);

                // Layer ContactSource onto self-registered users.
                if (existingUser.ContactSource is null)
                {
                    await userRepository.SetContactSourceIfNullAsync(existingUser.Id, source, ct);
                    existingUser.ContactSource = source;
                }

                return new AccountProvisioningResult(existingUser, Created: false);
            }
        }

        // Create new User + UserEmail.
        var resolvedDisplayName = string.IsNullOrWhiteSpace(displayName)
            ? email.Split('@')[0]
            : displayName;

        var now = clock.GetCurrentInstant();

        var newUserId = Guid.NewGuid();
        var newUser = new User
        {
            Id = newUserId,
            DisplayName = resolvedDisplayName,
            ContactSource = source,
            CreatedAt = now,
        };

        var result = await userManager.CreateAsync(newUser);
        if (!result.Succeeded)
        {
            var errors = string.Join(", ", result.Errors.Select(e => e.Description));
            throw new InvalidOperationException($"Failed to create account for {email}: {errors}");
        }

        // see nobodies-collective/Humans#687
        await userEmailService.AddProvisionedEmailAsync(newUser.Id, email, ct);

        // see #635 (§15i) — Stub Profile invariant; cross-section write via §2c.
        await profileService.EnsureStubProfileAsync(newUser.Id, ct);

        await auditLogService.LogAsync(
            AuditAction.ContactCreated,
            nameof(User), newUser.Id,
            $"Account pre-created from {source} — {email}",
            nameof(AccountProvisioningService));

        logger.LogInformation(
            "Created new account {UserId} for {Email} (source: {Source}, displayName: {DisplayName})",
            newUser.Id, email, source, resolvedDisplayName);

        return new AccountProvisioningResult(newUser, Created: true);
    }

    public async Task<MagicLinkSignupCompletionResult> CompleteMagicLinkSignupAsync(
        string email,
        string? displayName,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        var existingEmail = await userEmailService.FindVerifiedEmailWithUserAsync(email, ct);
        if (existingEmail is not null)
        {
            var existingUser = await userRepository.GetByIdAsync(existingEmail.UserId, ct);
            if (existingUser is null)
            {
                logger.LogWarning(
                    "Verified UserEmail references missing user {UserId} during magic-link signup for {Email}",
                    existingEmail.UserId, email);
                return new MagicLinkSignupCompletionResult(
                    MagicLinkSignupCompletionOutcome.Failed,
                    User: null);
            }

            existingUser.LastLoginAt = clock.GetCurrentInstant();
            await userManager.UpdateAsync(existingUser);
            return new MagicLinkSignupCompletionResult(
                MagicLinkSignupCompletionOutcome.ExistingUser,
                existingUser);
        }

        var now = clock.GetCurrentInstant();
        var user = new User
        {
            Id = Guid.NewGuid(),
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? email : displayName.Trim(),
            CreatedAt = now,
            LastLoginAt = now
        };

        var createResult = await userManager.CreateAsync(user);
        if (!createResult.Succeeded)
        {
            logger.LogError(
                "Failed to create user via magic link signup for {Email}: {Errors}",
                email,
                string.Join(", ", createResult.Errors.Select(e => e.Description)));
            return new MagicLinkSignupCompletionResult(
                MagicLinkSignupCompletionOutcome.Failed,
                User: null);
        }

        try
        {
            await userEmailService.AddVerifiedEmailAsync(user.Id, email, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to create UserEmail for magic-link signup {UserId} ({Email}); rolling back user",
                user.Id, email);
            await TryDeleteOrphanUserAsync(user);
            return new MagicLinkSignupCompletionResult(
                MagicLinkSignupCompletionOutcome.Failed,
                User: null);
        }

        await profileService.EnsureStubProfileAsync(user.Id, ct);

        logger.LogInformation(
            "Magic link signup: user {UserId} created account for {Email}",
            user.Id, email);

        return new MagicLinkSignupCompletionResult(
            MagicLinkSignupCompletionOutcome.Created,
            user);
    }

    private async Task TryDeleteOrphanUserAsync(User user)
    {
        try
        {
            await userManager.DeleteAsync(user);
        }
        catch (Exception deleteEx)
        {
            logger.LogError(deleteEx,
                "Failed to clean up orphan user {UserId} after AddVerifiedEmailAsync failure",
                user.Id);
        }
    }
}
