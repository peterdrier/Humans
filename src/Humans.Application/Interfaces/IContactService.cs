using Humans.Application.DTOs;
using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Application.Interfaces;

/// <summary>
/// Manages external contacts imported from MailerLite, TicketTailor, or manual entry.
/// Contacts are pre-provisioned Identity users with no credentials (LastLoginAt == null).
/// When they authenticate by any method, they claim their existing account — no merge needed.
/// </summary>
public interface IContactService
{
    /// <summary>
    /// Creates a pre-provisioned user for an external contact.
    /// Idempotent — returns the existing user if one already exists for the same email.
    /// </summary>
    Task<User> CreateContactAsync(
        string email, string displayName, ContactSource source,
        string? externalSourceId = null, CancellationToken ct = default);

    /// <summary>
    /// Returns all contacts (users with ContactSource set and LastLoginAt == null),
    /// optionally filtered by email/name search.
    /// </summary>
    Task<IReadOnlyList<AdminContactRow>> GetFilteredContactsAsync(
        string? search, CancellationToken ct = default);

    /// <summary>
    /// Gets a single contact by user ID with communication preferences loaded.
    /// </summary>
    Task<User?> GetContactDetailAsync(Guid userId, CancellationToken ct = default);
}
