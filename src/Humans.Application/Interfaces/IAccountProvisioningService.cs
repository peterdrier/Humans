using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Application.Interfaces;

/// <summary>
/// Provides idempotent account creation for import jobs (ticket import, MailerLite import).
/// Looks up existing accounts by email across all UserEmails for dedup,
/// creates User + UserEmail when no match exists.
/// </summary>
public interface IAccountProvisioningService
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
}

/// <summary>
/// Result of a find-or-create operation.
/// </summary>
/// <param name="User">The existing or newly created user.</param>
/// <param name="Created">True if a new account was created; false if an existing one was found.</param>
public record AccountProvisioningResult(User User, bool Created);
