
namespace Humans.Users.Contracts;

/// <summary>Idempotent account creation for the import jobs.</summary>
public interface IAccountProvisioningService
{
    /// <summary>
    /// Matches on <c>User.Email</c> and every <c>UserEmail</c> row. A blank <paramref name="displayName"/> falls
    /// back to the email prefix.
    /// </summary>
    Task<AccountProvisioningResult> FindOrCreateUserByEmailAsync(
        string email, string? displayName, ContactSource source,
        CancellationToken ct = default);

    /// <summary>
    /// Replaces the planned unverified, unlinked email row and provisions its contact account atomically. Refuses
    /// stale plans; a failure keeps the original row.
    /// </summary>
    Task<AccountProvisioningResult> ReplaceUnverifiedEmailAndProvisionAsync(
        Guid userId, Guid emailId, string email, string? displayName, ContactSource source,
        CancellationToken ct = default);

    /// <summary>
    /// After Auth has verified the token. A double submit signs in the existing owner; otherwise creates the user,
    /// its verified email and a stub profile, rolling back the user if the email row fails.
    /// </summary>
    Task<MagicLinkSignupCompletionResult> CompleteMagicLinkSignupAsync(
        string email,
        string burnerName,
        string firstName,
        string lastName,
        CancellationToken ct = default);
}

public record AccountProvisioningResult(Guid UserId, bool Created);

/// <summary><paramref name="UserId"/> is null when <paramref name="Outcome"/> is <see cref="MagicLinkSignupCompletionOutcome.Failed"/>.</summary>
public sealed record MagicLinkSignupCompletionResult(
    MagicLinkSignupCompletionOutcome Outcome,
    Guid? UserId);

public enum MagicLinkSignupCompletionOutcome
{
    Created,
    ExistingUser,
    Failed
}
