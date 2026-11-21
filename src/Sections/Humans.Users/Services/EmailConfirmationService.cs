using System.Security.Cryptography;
using Humans.Base.Helpers;
using Humans.Base.Interfaces;
using Humans.Email.Contracts;
using Humans.Users.Contracts;
using Humans.Users.Data.Repositories;
using Humans.Users.Helpers;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Users.Services;

/// <summary>What the emailed confirmation link may do for the account signed in to this browser.</summary>
internal enum EmailConfirmationState
{
    /// <summary>Forged, expired, already used, or its address is gone.</summary>
    Invalid,

    /// <summary>Genuine, but another account asked for it. Nothing about the address is revealed.</summary>
    WrongAccount,

    /// <summary>No other account has the address: one button confirms it.</summary>
    Confirm,

    /// <summary>The address is confirmed on another account: confirming merges the two.</summary>
    Merge,

    /// <summary>A merge is needed but one of the two accounts is rejected, suspended by an admin or being deleted.</summary>
    MergeBlocked,
}

/// <summary>What <see cref="EmailConfirmationService.ReadAsync"/> found behind a link.</summary>
/// <param name="State">What the link may do.</param>
/// <param name="AskingUserId">The account that added the address; empty when <see cref="EmailConfirmationState.Invalid"/>.</param>
/// <param name="Email">Set once the signed-in account is the asking one.</param>
/// <param name="OtherUserId">The account that already has the address confirmed.</param>
/// <param name="OtherHasNames">False for an account nobody filled in (no profile with names): there is nothing to choose, the asking account is kept.</param>
internal sealed record EmailConfirmation(
    EmailConfirmationState State,
    Guid AskingUserId = default,
    string Email = "",
    Guid? OtherUserId = null,
    bool OtherHasNames = false);

/// <summary>What <see cref="EmailConfirmationService.MergeAsync"/> did.</summary>
/// <param name="State">Anything but <see cref="EmailConfirmationState.Merge"/> means nothing was merged.</param>
/// <param name="KeptUserId">The account that remains, when the merge ran.</param>
/// <param name="ChoiceMissing">Both accounts are filled in and no account to keep was picked.</param>
internal sealed record EmailMergeOutcome(
    EmailConfirmationState State, Guid? KeptUserId = null, bool ChoiceMissing = false);

/// <summary>
/// The emailed confirmation link, for every added address. The link proves control of the mailbox; the
/// signed-in session proves control of the account that asked. Only both together confirm an address, and
/// only both together merge two accounts. Every check is re-run on each call, so the pages can be re-posted.
/// </summary>
internal sealed class EmailConfirmationService(
    IDataProtectionProvider dataProtection,
    IUserRepository repository,
    IUserManagement userService,
    IAccountMergeService mergeService,
    IServiceProvider serviceProvider,
    ILogger<EmailConfirmationService> logger) : IApplicationService
{
    internal static readonly TimeSpan LinkLifetime = TimeSpan.FromHours(1);

    private readonly ITimeLimitedDataProtector _protector =
        dataProtection.CreateProtector("Users.EmailConfirmation").ToTimeLimitedDataProtector();

    /// <summary>Bound to the asking account and the one pending row, valid for <see cref="LinkLifetime"/>.</summary>
    public string CreateToken(Guid askingUserId, Guid emailId) =>
        _protector.Protect($"{askingUserId:N}:{emailId:N}", LinkLifetime);

    public async Task<EmailConfirmation> ReadAsync(
        string? token, Guid signedInUserId, CancellationToken ct = default)
    {
        if (ParseToken(token) is not (var askingUserId, var emailId))
            return new(EmailConfirmationState.Invalid);

        // The table, not the cache: the decision must see this call's row state.
        var pending = await repository.GetUserEmailByIdAndUserIdAsync(emailId, askingUserId, ct);
        if (pending is null || pending.IsVerified || pending.Provider is not null)
            return new(EmailConfirmationState.Invalid);

        if (signedInUserId != askingUserId)
            return new(EmailConfirmationState.WrongAccount, askingUserId);

        var asking = await userService.GetRawUserInfoAsync(askingUserId, ct);
        if (asking is null || asking.IsTombstone)
            return new(EmailConfirmationState.Invalid);

        var normalized = EmailNormalization.NormalizeForComparison(pending.Email);
        var holder = (await repository.GetUserEmailsByAddressAsync(
                normalized, EmailLookupAliases.GetAlternate(normalized), ct))
            .FirstOrDefault(e => e.IsVerified && e.UserId != askingUserId);
        if (holder is null)
            return new(EmailConfirmationState.Confirm, askingUserId, pending.Email);

        var other = await userService.GetRawUserInfoAsync(holder.UserId, ct);
        var state = other is null || other.IsTombstone || BlocksMerge(asking) || BlocksMerge(other)
            ? EmailConfirmationState.MergeBlocked
            : EmailConfirmationState.Merge;
        return new(state, askingUserId, pending.Email, holder.UserId, other is { HasRequiredNameFields: true });
    }

    /// <summary>Confirms the address when no other account has it; any other state is returned untouched.</summary>
    public async Task<EmailConfirmation> ConfirmAsync(
        string? token, Guid signedInUserId, CancellationToken ct = default)
    {
        var confirmation = await ReadAsync(token, signedInUserId, ct);
        if (confirmation.State != EmailConfirmationState.Confirm)
            return confirmation;

        var (_, emailId) = ParseToken(token)!.Value;
        var update = await userService.UpdateUserEmailAsync(
            signedInUserId, emailId, new UserEmailUpdateCommand(MarkVerified: true), ct);
        if (!update.Succeeded)
        {
            logger.LogWarning("Email confirmation refused for user {UserId}, email row {EmailId}: {Reason}",
                signedInUserId, emailId, update.ErrorKey);
            return new(EmailConfirmationState.Invalid);
        }

        logger.LogInformation("User {UserId} confirmed email row {EmailId}", signedInUserId, emailId);
        return confirmation;
    }

    /// <summary>
    /// Merges the two accounts. The asking account is kept unless both are filled in, where
    /// <paramref name="keepUserId"/> must name one of the two.
    /// </summary>
    public async Task<EmailMergeOutcome> MergeAsync(
        string? token, Guid signedInUserId, Guid? keepUserId, CancellationToken ct = default)
    {
        var confirmation = await ReadAsync(token, signedInUserId, ct);
        if (confirmation.State != EmailConfirmationState.Merge || confirmation.OtherUserId is not { } otherUserId)
            return new(confirmation.State);

        var keptUserId = signedInUserId;
        if (confirmation.OtherHasNames)
        {
            if (keepUserId != signedInUserId && keepUserId != otherUserId)
                return new(EmailConfirmationState.Merge, ChoiceMissing: true);
            keptUserId = keepUserId.Value;
        }
        var archivedUserId = keptUserId == signedInUserId ? otherUserId : signedInUserId;

        // Before the merge: afterwards the archived account has no addresses left to tell.
        var notify = await GetConfirmedAddressesAsync([signedInUserId, otherUserId], confirmation.Email, ct);

        var (_, emailId) = ParseToken(token)!.Value;
        var result = await mergeService.MergeAsync(
            keptUserId, archivedUserId, signedInUserId,
            $"Self-service: {signedInUserId} confirmed {confirmation.Email}, which {otherUserId} already had.",
            keptUserId == signedInUserId ? emailId : null, ct);
        if (result.Refusal is { } refusal)
        {
            logger.LogWarning("Self-service merge refused for {Kept} <- {Archived}: {Reason}",
                keptUserId, archivedUserId, refusal);
            return new(EmailConfirmationState.MergeBlocked);
        }

        await SendMergeNoticesAsync(keptUserId, notify);
        return new(EmailConfirmationState.Merge, keptUserId);
    }

    private async Task<IReadOnlyList<string>> GetConfirmedAddressesAsync(
        IReadOnlyList<Guid> userIds, string confirmedNow, CancellationToken ct)
    {
        var addresses = new List<string> { confirmedNow };
        foreach (var userId in userIds)
            addresses.AddRange((await userService.GetRawUserInfoAsync(userId, ct))?.AllVerifiedEmails ?? []);
        return addresses.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    // The merge has committed: a mail fault must not read as a failed merge.
    private async Task SendMergeNoticesAsync(Guid keptUserId, IReadOnlyList<string> addresses)
    {
        try
        {
            var kept = await userService.GetUserInfoAsync(keptUserId, CancellationToken.None);
            var emails = serviceProvider.GetRequiredService<UsersEmails>();
            var sender = serviceProvider.GetRequiredService<IEmailService>();
            foreach (var address in addresses)
                await sender.SendAsync(
                    emails.AccountsMerged(keptUserId, address, kept?.BurnerName ?? string.Empty, kept?.PreferredLanguage),
                    CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Merge notice mail failed for kept account {UserId}", keptUserId);
        }
    }

    private static bool BlocksMerge(UserInfo user) =>
        user.State is UserState.Rejected or UserState.AdminSuspended or UserState.DeletePending;

    private (Guid AskingUserId, Guid EmailId)? ParseToken(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        try
        {
            var parts = _protector.Unprotect(token).Split(':');
            return parts.Length == 2
                && Guid.TryParseExact(parts[0], "N", out var userId)
                && Guid.TryParseExact(parts[1], "N", out var emailId)
                ? (userId, emailId)
                : null;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }
}
