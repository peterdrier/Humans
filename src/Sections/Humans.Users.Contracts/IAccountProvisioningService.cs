using NodaTime;

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
    /// stale plans; a metadata or commit failure keeps the original row. Best-effort success audits follow the commit.
    /// </summary>
    Task<AccountProvisioningResult> ReplaceUnverifiedEmailAndProvisionAsync(
        Guid userId, Guid emailId, string email, string? displayName, ContactSource source,
        CancellationToken ct = default);

    /// <summary>
    /// After Auth has verified the token. A double submit signs in the existing owner; otherwise creates the user,
    /// its verified email and a stub profile, rolling back the user if the email row fails.
    /// </summary>
    Task<SignInLinkSignupResult> CompleteSignInLinkSignupAsync(
        string email,
        string burnerName,
        string firstName,
        string lastName,
        CancellationToken ct = default);

    /// <summary>
    /// A bare account with no email, profile or password, for fixed system accounts and dev seed data. Returns
    /// Identity's error descriptions, empty on success. <paramref name="lastSignInAt"/> is seed fixture data only:
    /// a real sign-in records the login through <see cref="IUserWrite.RecordSignInAsync"/>.
    /// </summary>
    Task<IReadOnlyList<string>> CreateAccountAsync(
        Guid userId, string burnerName, Instant createdAt, Instant? lastSignInAt = null);
}

public record AccountProvisioningResult(Guid UserId, bool Created);

/// <summary><paramref name="UserId"/> is null when <paramref name="Outcome"/> is <see cref="SignInLinkSignupOutcome.Failed"/>.</summary>
public sealed record SignInLinkSignupResult(
    SignInLinkSignupOutcome Outcome,
    Guid? UserId);

public enum SignInLinkSignupOutcome
{
    Created,
    ExistingUser,
    Failed
}
