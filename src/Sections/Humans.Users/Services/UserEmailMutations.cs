using System.ComponentModel.DataAnnotations;
using Humans.Base.Extensions;
using Humans.Base.Helpers;
using Humans.Base.Interfaces;
using Humans.Base.Threading;
using Humans.Users.Contracts;
using Humans.Users.Data.Repositories;
using Humans.Users.Helpers;
using NodaTime;

namespace Humans.Users.Services;

internal sealed class UserEmailMutations(
    IUserRepository repo,
    IClock clock,
    ILogger<UserService> logger) : IApplicationService
{
    private static readonly TrackedLock EmailAdditionLock = new("UserService.EmailAddition");

    public async Task<UserEmailAddResult> AddUserEmailAsync(
        Guid userId,
        UserEmailAddCommand command,
        CancellationToken ct = default)
    {
        var email = command.Email.Trim();
        var normalizedEmail = EmailNormalization.NormalizeForComparison(email);
        var alternateEmail = EmailLookupAliases.GetAlternate(normalizedEmail);

        if (email.Length > 256 || !new EmailAddressAttribute().IsValid(email))
            return Refuse("Validation_EmailAddress");

        using var addition = await EmailAdditionLock.AcquireAsync(logger, ct);
        var rows = await repo.GetUserEmailsByAddressAsync(normalizedEmail, alternateEmail, ct);
        var existing = rows.FirstOrDefault(e => e.UserId == userId);
        if (existing is not null)
        {
            if (command.IgnoreExisting)
                return new UserEmailAddResult(existing.Id, Added: false, IsConflict: false);

            return Refuse("Users_Profile_EmailAlreadyAdded");
        }

        var isConflict = rows.Any(e => e.UserId != userId && e.IsVerified);
        // A verified insert has no merge flow to hand the conflict to.
        if (isConflict && command.IsVerified)
            return Refuse("Users_Profile_EmailVerifiedOnAnotherAccount");

        var user = await repo.GetByIdAsync(userId, ct);
        if (user is null || UserStateEvaluator.IsTombstone(user))
            return Refuse("Users_EmailUpdate_AccountUnavailable");

        var now = clock.GetCurrentInstant();
        var row = new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Email = email,
            IsVerified = command.IsVerified,
            IsPrimary = false,
            Visibility = command.Visibility,
            Provider = command.Provider,
            ProviderKey = command.ProviderKey,
            VerificationSentAt = command.VerificationSentAt,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await repo.AddUserEmailAsync(row, ct);
        // The row has committed; finish its required invariant repairs after a browser disconnect.
        await EnsurePrimaryInvariantAsync(row.UserId, CancellationToken.None);
        return new UserEmailAddResult(row.Id, Added: true, isConflict);

        UserEmailAddResult Refuse(string key)
        {
            logger.LogWarning("Email addition refused for user {UserId}: {ErrorKey}", userId, key);
            return new UserEmailAddResult(Guid.Empty, Added: false, IsConflict: false, ErrorKey: key);
        }
    }

    public async Task<UpdateEmailResult> UpdateUserEmailAsync(
        Guid userId,
        Guid emailId,
        UserEmailUpdateCommand command,
        CancellationToken ct = default)
    {
        var changed = false;

        if (command.MarkVerified)
        {
            var email = await repo.GetUserEmailByIdAndUserIdAsync(emailId, userId, ct);
            if (email is null) return new UpdateEmailResult(false, "Users_EmailUpdate_NotFound");

            if (email.IsVerified || email.Provider is not null)
                return new UpdateEmailResult(false, "Users_Profile_InvalidVerificationLink");

            email.IsVerified = true;
            email.UpdatedAt = clock.GetCurrentInstant();
            await repo.UpdateUserEmailAsync(email, ct);
            // Verification has committed; primary repair is part of completing it.
            await EnsurePrimaryInvariantAsync(userId, CancellationToken.None);
            changed = true;
        }

        if (command.Primary == UserEmailPrimaryChange.MakePrimary)
        {
            var primary = await SetPrimaryEmailAsync(userId, emailId, ct);
            if (!primary.Succeeded) return primary;
            changed = true;
        }
        else if (command.Primary == UserEmailPrimaryChange.ClearDuplicatePrimary)
        {
            changed |= await ClearDuplicatePrimaryAsync(userId, emailId, ct);
        }

        if (command.ChangeVisibility)
        {
            var row = await repo.GetUserEmailByIdAndUserIdAsync(emailId, userId, ct);
            if (row is null) return new UpdateEmailResult(false, "Users_EmailUpdate_NotFound");
            row.Visibility = command.Visibility;
            row.UpdatedAt = clock.GetCurrentInstant();
            await repo.UpdateUserEmailAsync(row, ct);
            changed = true;
        }

        return new UpdateEmailResult(changed);
    }

    public async Task<bool> RemoveUserEmailAsync(
        Guid userId,
        Guid emailId,
        UserEmailRemoveCommand command,
        CancellationToken ct = default)
    {
        var email = await repo.GetUserEmailByIdAndUserIdAsync(emailId, userId, ct)
            ?? throw new InvalidOperationException("Email not found.");

        var hasProvider = !string.IsNullOrEmpty(email.Provider);
        if (command.Mode == UserEmailRemovalMode.PlainEmail && hasProvider)
            return false;
        if (command.Mode == UserEmailRemovalMode.ProviderLinkedEmail && !hasProvider)
            return false;

        if (command.PreserveLastVerifiedEmail && email.IsVerified)
        {
            var allEmails = await repo.GetUserEmailsByUserIdForMutationAsync(userId, ct);
            var verifiedRemaining = allEmails.Count(e => e.IsVerified && e.Id != emailId);

            if (verifiedRemaining == 0)
            {
                throw new ValidationException(
                    "Cannot remove your last verified email. Add another verified email first " +
                    "so you can still receive system notifications.");
            }
        }

        await repo.RemoveUserEmailAsync(email, ct);

        if (command.RepairInvariants)
        {
            // Removal has committed; leave the remaining verified addresses usable.
            await EnsurePrimaryInvariantAsync(userId, CancellationToken.None);
        }

        return true;
    }

    private async Task<UpdateEmailResult> SetPrimaryEmailAsync(Guid userId, Guid emailId, CancellationToken ct)
    {
        var emails = (await repo.GetUserEmailsByUserIdForMutationAsync(userId, ct)).ToList();

        var target = emails.FirstOrDefault(e => e.Id == emailId);
        if (target is null) return new UpdateEmailResult(false, "Users_EmailUpdate_NotFound");

        if (!target.IsVerified)
            return new UpdateEmailResult(false, "Users_EmailUpdate_UnverifiedTarget");

        var now = clock.GetCurrentInstant();
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

        if (changed.Count > 0)
            await repo.UpdateUserEmailsAsync(changed, ct);
        return new UpdateEmailResult(true);
    }

    private async Task<bool> ClearDuplicatePrimaryAsync(Guid userId, Guid emailId, CancellationToken ct)
    {
        var row = await repo.GetUserEmailByIdAndUserIdAsync(emailId, userId, ct);
        if (row is null || !row.IsPrimary) return false;

        var allEmails = await repo.GetUserEmailsByUserIdReadOnlyAsync(userId, ct);
        if (allEmails.Count(e => e.IsPrimary && e.IsVerified) <= 1) return false;

        row.IsPrimary = false;
        row.UpdatedAt = clock.GetCurrentInstant();
        await repo.UpdateUserEmailAsync(row, ct);
        return true;
    }

    internal async Task EnsurePrimaryInvariantAsync(Guid userId, CancellationToken ct)
    {
        var emails = (await repo.GetUserEmailsByUserIdForMutationAsync(userId, ct)).ToList();
        var verified = emails.Where(e => e.IsVerified).ToList();
        if (verified.Count == 0)
            return;

        var currentPrimaries = verified.Where(e => e.IsPrimary).ToList();

        var winner =
            verified.FirstOrDefault(e => e.Email.EndsWith("@nobodies.team", StringComparison.OrdinalIgnoreCase))
            ?? currentPrimaries.OrderBy(e => e.Id).FirstOrDefault()
            ?? verified.OrderByDescending(e => e.UpdatedAt).ThenBy(e => e.Id).First();

        if (currentPrimaries.Count == 1 && currentPrimaries[0].Id == winner.Id)
            return;

        var now = clock.GetCurrentInstant();
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

        if (changed.Count > 0)
            await repo.UpdateUserEmailsAsync(changed, ct);
    }

}
