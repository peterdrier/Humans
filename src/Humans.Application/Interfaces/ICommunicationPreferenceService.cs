using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Application.Interfaces;

/// <summary>
/// Manages per-user communication preferences and unsubscribe tokens.
/// </summary>
public interface ICommunicationPreferenceService
{
    /// <summary>
    /// Returns all preferences for a user, creating defaults for any missing categories.
    /// </summary>
    Task<IReadOnlyList<CommunicationPreference>> GetPreferencesAsync(
        Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns whether a user has opted out of a specific category.
    /// </summary>
    Task<bool> IsOptedOutAsync(
        Guid userId, MessageCategory category, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns whether a user accepts facilitated messages (i.e. has NOT opted out of FacilitatedMessages).
    /// Used to gate the Send Message function.
    /// </summary>
    Task<bool> AcceptsFacilitatedMessagesAsync(
        Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates opt-out status for a specific category. Idempotent.
    /// Logs an audit entry for compliance.
    /// </summary>
    Task UpdatePreferenceAsync(
        Guid userId, MessageCategory category, bool optedOut, string source,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates opt-out and inbox-enabled status for a specific category. Idempotent.
    /// Logs an audit entry for compliance.
    /// </summary>
    Task UpdatePreferenceAsync(
        Guid userId, MessageCategory category, bool optedOut, bool inboxEnabled, string source,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates a time-limited unsubscribe token encoding userId + category.
    /// Token expires after ~90 days.
    /// </summary>
    string GenerateUnsubscribeToken(Guid userId, MessageCategory category);

    /// <summary>
    /// Validates and decodes an unsubscribe token.
    /// Returns null if the token is invalid or expired.
    /// </summary>
    (Guid UserId, MessageCategory Category)? ValidateUnsubscribeToken(string token);

    /// <summary>
    /// Generates RFC 8058 List-Unsubscribe headers for a given user and category.
    /// Returns a dictionary of header name → value pairs.
    /// </summary>
    Dictionary<string, string> GenerateUnsubscribeHeaders(Guid userId, MessageCategory category);
}
