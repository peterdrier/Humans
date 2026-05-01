using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Interfaces.Profiles;

/// <summary>
/// Status of a CommunicationPreference token validation attempt.
/// Distinct from <see cref="UnsubscribeTokenResult"/> which is the high-level
/// result returned by <see cref="IUnsubscribeService"/>.
/// </summary>
public enum TokenValidationStatus
{
    /// <summary>Token is valid and decoded successfully.</summary>
    Valid,

    /// <summary>Token was a valid new-format token but has expired.</summary>
    Expired,

    /// <summary>Token is not a valid new-format token (tampered, corrupted, or different format).</summary>
    Invalid,
}

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
    /// Returns status (Valid/Expired/Invalid) with decoded UserId and Category when valid.
    /// </summary>
    (TokenValidationStatus Status, Guid UserId, MessageCategory Category) ValidateUnsubscribeToken(string token);

    /// <summary>
    /// Generates RFC 8058 List-Unsubscribe headers for a given user and category.
    /// Returns a dictionary of header name → value pairs.
    /// </summary>
    Dictionary<string, string> GenerateUnsubscribeHeaders(Guid userId, MessageCategory category);

    /// <summary>
    /// Returns a set of user IDs (from the input list) whose inbox is disabled
    /// for the given category. Users with no preference row are considered inbox-enabled.
    /// </summary>
    Task<IReadOnlySet<Guid>> GetUsersWithInboxDisabledAsync(
        IReadOnlyList<Guid> userIds, MessageCategory category,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns whether any communication preferences exist for the given user.
    /// </summary>
    Task<bool> HasAnyPreferencesAsync(
        Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the set of user IDs (from the input list) that have any communication preferences.
    /// </summary>
    Task<IReadOnlySet<Guid>> GetUsersWithAnyPreferencesAsync(
        IReadOnlyList<Guid> userIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates a browser-friendly unsubscribe URL for use in email footers.
    /// Unlike <see cref="GenerateUnsubscribeHeaders"/>, this returns a plain URL string
    /// suitable for direct use in anchor tags.
    /// </summary>
    string GenerateBrowserUnsubscribeUrl(Guid userId, MessageCategory category);

    /// <summary>
    /// Account-merge fold: bulk-moves <c>CommunicationPreference</c> rows from
    /// <paramref name="sourceUserId"/> to <paramref name="targetUserId"/>.
    /// Same-category rows collapse — the row with the most-recent
    /// <c>UpdatedAt</c> wins (source's values are copied onto target when source
    /// is newer; otherwise the source row is dropped). Surviving source rows
    /// are re-FK'd to target. Stamps <c>UpdatedAt</c> on every row touched.
    /// Invalidates the FullProfile cache for both users so admin/search/profile
    /// surfaces reflect the move. Returns the count of
    /// <c>CommunicationPreference</c> rows attributed to
    /// <paramref name="targetUserId"/>. Called only by
    /// <c>AccountMergeService.AcceptAsync</c>.
    /// </summary>
    Task<int> ReassignToUserAsync(
        Guid sourceUserId,
        Guid targetUserId,
        Instant updatedAt,
        CancellationToken cancellationToken = default);
}
