using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Application.Interfaces.Users;

/// <summary>
/// Provides idempotent account creation for import jobs (ticket import, MailerLite import).
/// Looks up existing accounts by email across all UserEmails for dedup,
/// creates User + UserEmail when no match exists.
/// </summary>
public interface IAccountProvisioningService : IApplicationService
{
    /// <summary>
    /// Finds an existing User by email (checking both User.Email and all UserEmail records)
    /// or creates a new User + UserEmail if no match exists. Idempotent.
    /// </summary>
    /// <param name="email">The email address to look up or create an account for.</param>
    /// <param name="displayName">Display name for the new account. Falls back to email prefix if null/empty.</param>
    /// <param name="source">Where this contact was imported from.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The existing or newly created User, and whether a new account was created.</returns>
    Task<AccountProvisioningResult> FindOrCreateUserByEmailAsync(
        string email, string? displayName, ContactSource source,
        CancellationToken ct = default);

    /// <summary>
    /// Completes a magic-link signup after the Auth section has verified the
    /// signup token. Idempotently signs in the existing verified-email owner
    /// on double submit, otherwise creates User + verified UserEmail + stub
    /// Profile and rolls back the User if the email row cannot be created.
    /// </summary>
    Task<MagicLinkSignupCompletionResult> CompleteMagicLinkSignupAsync(
        string email,
        string? displayName,
        CancellationToken ct = default);
}

/// <summary>
/// Result of a find-or-create operation.
/// </summary>
/// <param name="User">The existing or newly created user.</param>
/// <param name="Created">True if a new account was created; false if an existing one was found.</param>
public record AccountProvisioningResult(User User, bool Created);

public sealed record MagicLinkSignupCompletionResult(
    MagicLinkSignupCompletionOutcome Outcome,
    User? User);

public enum MagicLinkSignupCompletionOutcome
{
    Created,
    ExistingUser,
    Failed
}
