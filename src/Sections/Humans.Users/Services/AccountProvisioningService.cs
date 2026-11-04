using System.Transactions;
using Humans.AuditLog.Contracts;
using Humans.Users.Data.Repositories;
using Humans.Users.Contracts;
using Humans.Users.Data;
using Microsoft.AspNetCore.Identity;
using NodaTime;

namespace Humans.Users.Services;

// Idempotent account provisioning for import jobs. UserManager allowed per the §2a exception.
internal sealed class AccountProvisioningService(
    IUserRepository userRepository,
    IUserEmailService userEmailService,
    IUserManagement userService,
    IUserInfoSliceRefresher userInfoRefresher,
    UserManager<User> userManager,
    IAuditLogService auditLogService,
    IClock clock,
    ILogger<AccountProvisioningService> logger) : IAccountProvisioningService
{
    public async Task<AccountProvisioningResult> FindOrCreateUserByEmailAsync(
        string email, string? displayName, ContactSource source, CancellationToken ct = default)
    {
        var (result, _) = await FindOrCreateCoreAsync(email, displayName, source, ct);
        return result;
    }

    public async Task<AccountProvisioningResult> ReplaceUnverifiedEmailAndProvisionAsync(
        Guid userId, Guid emailId, string email, string? displayName, ContactSource source,
        CancellationToken ct = default)
    {
        var affectedUsers = new HashSet<Guid> { userId };
        try
        {
            (AccountProvisioningResult Result, bool ProfileBackfilled) provisioned;
            using (var transaction = new TransactionScope(TransactionScopeOption.Required,
                new TransactionOptions { IsolationLevel = IsolationLevel.Serializable },
                TransactionScopeAsyncFlowOption.Enabled))
            {
                var rows = await userEmailService.FindByAddressAsync(email, aliased: true, verifiedOnly: false, ct);
                var row = rows.SingleOrDefault(r => r.Id == emailId && r.UserId == userId);
                if (rows.Count != 1 || row is null || row.IsVerified || !string.IsNullOrEmpty(row.Provider))
                    throw new InvalidOperationException("The unverified email replacement plan is stale.");
                if (!(await userEmailService.DeleteEmailAsync(userId, emailId, ct)).Succeeded)
                    throw new InvalidOperationException("The planned unverified email could not be removed.");
                provisioned = await FindOrCreateCoreAsync(email, displayName, source, ct, affectedUsers);
                transaction.Complete();
            }

            await AuditProvisioningAsync(provisioned.Result, provisioned.ProfileBackfilled, email, source);
            await auditLogService.LogAsync(UsersAuditActions.UserEmailDeleted, nameof(UserEmail), emailId,
                $"Replaced unverified email {email} with verified import account {provisioned.Result.UserId} from {source}",
                nameof(AccountProvisioningService), relatedEntityId: userId, relatedEntityType: nameof(User));
            return provisioned.Result;
        }
        finally
        {
            // Rebuild after the transaction is disposed, including on rollback, so no cached
            // entry keeps uncommitted row state.
            foreach (var affectedUser in affectedUsers)
                await userInfoRefresher.RefreshAsync(affectedUser, CancellationToken.None);
        }
    }

    private async Task<(AccountProvisioningResult Result, bool ProfileBackfilled)> FindOrCreateCoreAsync(
        string email, string? displayName, ContactSource source, CancellationToken ct,
        HashSet<Guid>? affectedUsers = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        var matchingUserId = (await userEmailService.FindByAddressAsync(email, aliased: true, verifiedOnly: false, ct))
            .FirstOrDefault()?.UserId;

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

                affectedUsers?.Add(existingUser.Id);

                if (existingUser.ContactSource is null)
                    await userRepository.SetContactSourceIfNullAsync(existingUser.Id, source, ct);

                // User/email may have committed before profile creation failed. Retry the
                // idempotent final step before reporting a successfully provisioned account.
                var profileBackfilled = await userService.EnsureStubProfileAsync(existingUser.Id, ct: ct);
                var result = new AccountProvisioningResult(existingUser.Id, Created: false);
                if (affectedUsers is null)
                    await AuditProvisioningAsync(result, profileBackfilled, email, source);

                return (result, profileBackfilled);
            }
        }

        var resolvedDisplayName = string.IsNullOrWhiteSpace(displayName)
            ? email.Split('@')[0]
            : displayName;

        var now = clock.GetCurrentInstant();

        var newUserId = Guid.NewGuid();
        affectedUsers?.Add(newUserId);
        var newUser = new User
        {
            Id = newUserId,
            BurnerName = resolvedDisplayName,
            ContactSource = source,
            CreatedAt = now,
        };

        try
        {
            using (var transaction = new TransactionScope(TransactionScopeOption.Required,
                new TransactionOptions { IsolationLevel = IsolationLevel.Serializable },
                TransactionScopeAsyncFlowOption.Enabled))
            {
                var result = await userManager.CreateAsync(newUser);
                if (!result.Succeeded)
                {
                    var errors = string.Join(", ", result.Errors.Select(e => e.Description));
                    throw new InvalidOperationException($"Failed to create account for {email}: {errors}");
                }

                await userEmailService.AddProvisionedEmailAsync(newUser.Id, email, ct);

                // The import seeds no names; onboarding fills the stub.
                await userService.EnsureStubProfileAsync(newUser.Id, ct: ct);
                transaction.Complete();
            }

            var provisioned = new AccountProvisioningResult(newUser.Id, Created: true);
            // Replacement imports own the outer commit and emit these audits afterward.
            if (affectedUsers is null)
                await AuditProvisioningAsync(provisioned, profileBackfilled: false, email, source);

            logger.LogInformation(
                "Created new account {UserId} for {Email} (source: {Source}, displayName: {DisplayName})",
                newUser.Id, email, source, resolvedDisplayName);

            return (provisioned, ProfileBackfilled: false);
        }
        finally
        {
            // The replacement path's outer transaction refreshes after its disposal.
            // Ordinary imports finish their own commit/rollback before rebuilding UserInfo.
            if (affectedUsers is null)
                await userInfoRefresher.RefreshAsync(newUserId, CancellationToken.None);
        }
    }

    private Task AuditProvisioningAsync(
        AccountProvisioningResult result, bool profileBackfilled, string email, ContactSource source)
    {
        if (result.Created)
            return auditLogService.LogAsync(UsersAuditActions.ContactCreated, nameof(User), result.UserId,
                $"Account pre-created from {source} — {email}", nameof(AccountProvisioningService));

        return profileBackfilled
            ? auditLogService.LogAsync(UsersAuditActions.StubProfileBackfilled, nameof(User), result.UserId,
                $"Completed missing profile provisioning from {source}.", nameof(AccountProvisioningService))
            : Task.CompletedTask;
    }

    public async Task<MagicLinkSignupCompletionResult> CompleteMagicLinkSignupAsync(
        string email,
        string burnerName,
        string firstName,
        string lastName,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(burnerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(firstName);
        ArgumentException.ThrowIfNullOrWhiteSpace(lastName);

        burnerName = burnerName.Trim();
        firstName = firstName.Trim();
        lastName = lastName.Trim();
        if (burnerName.Length > 256 || firstName.Length > 256 || lastName.Length > 256)
        {
            logger.LogWarning("Magic-link signup refused: a name exceeds the 256-character limit");
            return new MagicLinkSignupCompletionResult(MagicLinkSignupCompletionOutcome.Failed, UserId: null);
        }

        var existingEmail = (await userEmailService.FindByAddressAsync(email, aliased: true, verifiedOnly: true, ct))
            .FirstOrDefault();
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
                    UserId: null);
            }

            if (await userService.EnsureStubProfileAsync(
                existingUser.Id, burnerName, firstName, lastName, ct))
                await auditLogService.LogAsync(UsersAuditActions.StubProfileBackfilled, nameof(User), existingUser.Id,
                    "Completed missing profile after interrupted magic-link signup.", existingUser.Id);
            await userService.RecordLoginAsync(existingUser.Id, ct);
            return new MagicLinkSignupCompletionResult(
                MagicLinkSignupCompletionOutcome.ExistingUser,
                existingUser.Id);
        }

        var now = clock.GetCurrentInstant();
        var user = new User
        {
            Id = Guid.NewGuid(),
            BurnerName = burnerName,
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
                UserId: null);
        }

        try
        {
            var emailAddition = await userEmailService.AddVerifiedEmailAsync(user.Id, email, ct);
            if (emailAddition.ErrorKey is not null)
                throw new InvalidOperationException("Could not add the verified email address.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to create UserEmail for magic-link signup {UserId} ({Email}); rolling back user",
                user.Id, email);
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
            return new MagicLinkSignupCompletionResult(
                MagicLinkSignupCompletionOutcome.Failed,
                UserId: null);
        }

        await userService.EnsureStubProfileAsync(
            user.Id, burnerName, firstName, lastName, ct);

        logger.LogInformation(
            "Magic link signup: user {UserId} created account for {Email}",
            user.Id, email);

        return new MagicLinkSignupCompletionResult(
            MagicLinkSignupCompletionOutcome.Created,
            user.Id);
    }

    public async Task<IReadOnlyList<string>> CreateAccountAsync(
        Guid userId, string burnerName, Instant createdAt, Instant? lastLoginAt = null)
    {
        var result = await userManager.CreateAsync(new User
        {
            Id = userId,
            BurnerName = burnerName,
            CreatedAt = createdAt,
            LastLoginAt = lastLoginAt
        });
        return [.. result.Errors.Select(e => e.Description)];
    }
}
