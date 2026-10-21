using Humans.Base.Interfaces;
using NodaTime;

namespace Humans.Email.Contracts;

/// <summary>
/// Reads of a member's communication preferences (<c>email_communication_preferences</c>, owned by
/// Email since Users decomposition commit 14). Every read resolves a merged-away id forward to its
/// survivor. Writes other sections make go through <see cref="ICommunicationPreferenceWrite"/>; the
/// member's own toggles, the unsubscribe tokens and the copy stay internal to Email.
/// </summary>
public interface ICommunicationPreferenceService : IApplicationService
{
    /// <summary>
    /// Returns the existing preference for a user+category, or null if none is recorded.
    /// Read-only — does NOT lazy-create defaults. Use this when the caller
    /// needs to know whether a preference exists (e.g. the MailerLite importer's plan
    /// phase, which must not mutate state during preview).
    /// </summary>
    Task<CommunicationPreferenceSnapshot?> GetPreferenceOrNullAsync(
        Guid userId, MessageCategory category, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the user's recorded preferences. Read-only — does NOT lazy-create defaults for
    /// missing categories, so merely viewing a profile writes nothing. Callers that need a value
    /// for a missing category fall back to the category's natural default.
    /// </summary>
    Task<IReadOnlyList<CommunicationPreferenceSnapshot>> GetPreferencesReadOnlyAsync(
        Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every user's recorded preference for one category, keyed by user id. A user with no
    /// entry has no preference recorded (for Marketing: neither opted in nor out). Used by the
    /// MailerLite audiences and import, and the Debug user grid.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, CommunicationPreferenceSnapshot>> GetPreferencesForCategoryAsync(
        MessageCategory category, CancellationToken cancellationToken = default);

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
    /// Returns a set of user IDs (from the input list) whose inbox is disabled
    /// for the given category. Users with no preference are considered inbox-enabled.
    /// </summary>
    Task<IReadOnlySet<Guid>> GetUsersWithInboxDisabledAsync(
        IReadOnlyList<Guid> userIds, MessageCategory category,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the count of preferences matching the given category and opt-out state.
    /// Used for dashboard metrics.
    /// </summary>
    Task<int> GetCountByCategoryAndStateAsync(
        MessageCategory category, bool optedOut, CancellationToken cancellationToken = default);
}

/// <summary>
/// The communication-preference writes another section makes: the MailerLite import's
/// Marketing sync and its GDPR remediation. Both audit the change.
/// </summary>
public interface ICommunicationPreferenceWrite : IApplicationService
{
    /// <summary>
    /// Updates opt-out status for a specific category, keeping the inbox setting. Idempotent.
    /// Logs an audit entry for compliance.
    /// </summary>
    Task UpdatePreferenceAsync(
        Guid userId, MessageCategory category, bool optedOut, string source,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reverts the category to the "no preference recorded" (null) state — distinct from an
    /// explicit opt-out. No-op if nothing is recorded. Logs an audit entry when a preference is
    /// removed. Used by the MailerLite import GDPR remediation to undo Marketing opt-ins the
    /// erroneous whole-account import set.
    /// </summary>
    Task ResetPreferenceAsync(
        Guid userId, MessageCategory category, string source,
        CancellationToken cancellationToken = default);
}

public sealed record CommunicationPreferenceSnapshot(
    MessageCategory Category,
    bool OptedOut,
    bool InboxEnabled,
    string UpdateSource,
    Instant UpdatedAt,
    Instant? SubscribedAt = null);
