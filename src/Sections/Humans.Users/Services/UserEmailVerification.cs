using System.ComponentModel.DataAnnotations;
using Humans.AuditLog.Contracts;
using Humans.Base.Helpers;
using Humans.Base.Interfaces;
using Humans.Users.Contracts;
using Humans.Users.Data.Repositories;
using Humans.Users.Helpers;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Users.Services;

internal sealed class UserEmailVerification(
    IUserRepository repository,
    IUserManagement userService,
    UserManager<User> userManager,
    IClock clock,
    IAuditLogService auditLogService,
    IServiceProvider serviceProvider,
    ILogger<UserEmailService> logger) : IApplicationService
{
    private const string EmailVerificationTokenPurpose = "UserEmailVerification";

    // Lazy: breaks DI cycle TeamService -> IEmailService -> IUserEmailService -> IAccountMergeService -> ITeamService.
    private IAccountMergeService MergeService => serviceProvider.GetRequiredService<IAccountMergeService>();

    internal async Task<AddEmailResult> AddEmailAsync(
        Guid userId, string email, CancellationToken cancellationToken = default)
    {
        email = email.Trim();
        var normalizedEmail = EmailNormalization.NormalizeForComparison(email);
        var alternateEmail = EmailLookupAliases.GetAlternate(normalizedEmail);

        if (!new EmailAddressAttribute().IsValid(email))
            return Refuse("Validation_EmailAddress");

        var held = await repository.GetUserEmailsByAddressAsync(normalizedEmail, alternateEmail, cancellationToken);
        if (held.Any(e => e.UserId == userId))
            return Refuse("Users_Profile_EmailAlreadyAdded");

        if (await MergeService.HasPendingForUserAndEmailAsync(
                userId, normalizedEmail, alternateEmail, cancellationToken))
            return Refuse("Users_Profile_EmailMergePending");

        // FindByIdAsync, not cache: UserManager token APIs read SecurityStamp directly and cache-rehydrated Users lack it.
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null)
            return Refuse("Users_EmailUpdate_AccountUnavailable");

        // Through IUserManagement so its caching decorator refreshes UserInfo.
        var addResult = await userService.AddUserEmailAsync(
            userId,
            new UserEmailAddCommand(
                email,
                IsVerified: false,
                VerificationSentAt: clock.GetCurrentInstant(),
                IgnoreExisting: true),
            cancellationToken);

        if (addResult.ErrorKey is { } errorKey)
            return new AddEmailResult(Guid.Empty, string.Empty, false, errorKey);

        if (!addResult.Added)
            return Refuse("Users_Profile_EmailAlreadyAdded");

        var token = await userManager.GenerateUserTokenAsync(
            user,
            TokenOptions.DefaultEmailProvider,
            $"{EmailVerificationTokenPurpose}:{addResult.EmailId}");

        return new AddEmailResult(addResult.EmailId, token, addResult.IsConflict);

        AddEmailResult Refuse(string key)
        {
            logger.LogWarning("Email addition refused for user {UserId}: {ErrorKey}", userId, key);
            return new AddEmailResult(Guid.Empty, string.Empty, false, key);
        }
    }

    internal async Task<VerifyEmailResult> VerifyEmailAsync(
        Guid userId, Guid emailId, string token, CancellationToken cancellationToken = default)
    {
        // Not the cache: a UserInfo-rehydrated User lacks the SecurityStamp that
        // VerifyUserTokenAsync reads.
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null) return Refuse("User not found");

        // The token is bound to this row's Id via the purpose suffix.
        var pendingEmail = await repository.GetUserEmailByIdAndUserIdAsync(emailId, userId, cancellationToken);
        if (pendingEmail is null || pendingEmail.IsVerified || pendingEmail.Provider is not null)
        {
            return Refuse("No email pending verification");
        }

        var isValid = await userManager.VerifyUserTokenAsync(
            user,
            TokenOptions.DefaultEmailProvider,
            $"{EmailVerificationTokenPurpose}:{pendingEmail.Id}",
            token);

        if (!isValid)
            return Refuse("Invalid verification token");

        if (await RequestMergeForVerificationConflictAsync(userId, pendingEmail, cancellationToken))
            return new VerifyEmailResult(pendingEmail.Email, MergeRequestCreated: true);

        // Flipping unverified→verified may newly satisfy the primary invariant.
        var update = await userService.UpdateUserEmailAsync(
            userId,
            pendingEmail.Id,
            new UserEmailUpdateCommand(MarkVerified: true),
            cancellationToken);
        if (!update.Succeeded) return Refuse("Email update refused");

        return new VerifyEmailResult(pendingEmail.Email, MergeRequestCreated: false);

        VerifyEmailResult Refuse(string reason)
        {
            logger.LogWarning("Email verification rejected for user {UserId}, email row {EmailId}: {Reason}",
                userId, emailId, reason);
            return new VerifyEmailResult(string.Empty, false, "Users_Profile_InvalidVerificationLink");
        }
    }

    internal async Task<VerifyEmailResult> AdminMarkVerifiedAsync(
        Guid userId, Guid emailId, Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var pendingEmail = await repository.GetUserEmailByIdAndUserIdAsync(emailId, userId, cancellationToken);
        if (pendingEmail is null || pendingEmail.IsVerified || pendingEmail.Provider is not null)
        {
            return new VerifyEmailResult(string.Empty, false) { NoPendingVerification = true };
        }

        // As in VerifyEmailAsync: an address verified on another account becomes a merge request.
        if (await RequestMergeForVerificationConflictAsync(userId, pendingEmail, cancellationToken))
            return new VerifyEmailResult(pendingEmail.Email, MergeRequestCreated: true);

        var update = await userService.UpdateUserEmailAsync(
            userId,
            pendingEmail.Id,
            new UserEmailUpdateCommand(MarkVerified: true),
            cancellationToken);
        if (!update.Succeeded) return new VerifyEmailResult(string.Empty, false, update.ErrorKey);

        await auditLogService.LogAsync(
            UsersAuditActions.UserEmailManuallyVerified,
            nameof(User), userId,
            $"Admin manually verified email {pendingEmail.Email}",
            actorUserId,
            relatedEntityId: pendingEmail.Id, relatedEntityType: nameof(UserEmail));

        return new VerifyEmailResult(pendingEmail.Email, MergeRequestCreated: false);
    }

    // Reads the table, not the cache: the decision must see this call's row state. Any verified
    // row for the same address on another row id, either user's, is the conflict.
    private async Task<bool> RequestMergeForVerificationConflictAsync(
        Guid userId, UserEmail pendingEmail, CancellationToken cancellationToken)
    {
        var normalized = EmailNormalization.NormalizeForComparison(pendingEmail.Email);
        var rows = await repository.GetUserEmailsByAddressAsync(
            normalized, EmailLookupAliases.GetAlternate(normalized), cancellationToken);
        var conflictingEmail = rows.FirstOrDefault(e => e.IsVerified && e.Id != pendingEmail.Id);
        if (conflictingEmail is null)
            return false;

        // Link prefetch, double-clicks and repeat admin verification would duplicate it.
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

        return true;
    }
}
