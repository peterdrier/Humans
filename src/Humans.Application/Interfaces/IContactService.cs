using Humans.Application.DTOs;
using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Application.Interfaces;

/// <summary>
/// Manages lightweight external contacts (MailerLite, TicketTailor, manual).
/// Contacts exist for communication preference tracking but cannot log in.
/// </summary>
public interface IContactService
{
    /// <summary>
    /// Creates a contact account. Idempotent — returns the existing contact if one
    /// already exists for the same email. Throws if a full member already has this email.
    /// </summary>
    Task<User> CreateContactAsync(
        string email, string displayName, ContactSource source,
        string? externalSourceId = null, CancellationToken ct = default);

    /// <summary>
    /// Finds an existing contact by email (normalized comparison).
    /// Returns null if no contact exists with this email.
    /// </summary>
    Task<User?> FindContactByEmailAsync(string email, CancellationToken ct = default);

    /// <summary>
    /// Finds an existing contact by external system source and ID.
    /// </summary>
    Task<User?> FindContactByExternalIdAsync(
        ContactSource source, string externalSourceId, CancellationToken ct = default);

    /// <summary>
    /// Returns all contacts, optionally filtered by email/name search.
    /// </summary>
    Task<IReadOnlyList<AdminContactRow>> GetFilteredContactsAsync(
        string? search, CancellationToken ct = default);

    /// <summary>
    /// Gets a single contact by user ID with communication preferences loaded.
    /// </summary>
    Task<User?> GetContactDetailAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Merges a contact into a member account. Migrates communication preferences
    /// (member's existing preferences win on conflict), migrates UserEmails, and
    /// deactivates the contact account. Logs audit entries on both accounts.
    /// </summary>
    Task MergeContactToMemberAsync(
        User contactUser, User memberUser,
        Guid? actorUserId, string actorName, CancellationToken ct = default);
}
