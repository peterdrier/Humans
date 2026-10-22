using Humans.Base.Interfaces;
using NodaTime;

namespace Humans.Email.Contracts;

/// <summary>
/// Reads of a member's per-category communication preferences. Every read resolves a merged-away id to
/// its survivor. Reads never create rows: a missing category reads as <see cref="MessageCategoryExtensions.DefaultOptedOut"/>.
/// </summary>
public interface ICommunicationPreferenceService : IApplicationService
{
    /// <summary>Null when nothing is recorded, so a caller can tell "no preference" from a default.</summary>
    Task<CommunicationPreferenceSnapshot?> GetPreferenceOrNullAsync(
        Guid userId, MessageCategory category, CancellationToken cancellationToken = default);

    /// <summary>Recorded rows only; missing categories are left out.</summary>
    Task<IReadOnlyList<CommunicationPreferenceSnapshot>> GetPreferencesReadOnlyAsync(
        Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Keyed by user id. A user with no entry has nothing recorded (for Marketing: neither opted in nor out).
    /// </summary>
    Task<IReadOnlyDictionary<Guid, CommunicationPreferenceSnapshot>> GetPreferencesForCategoryAsync(
        MessageCategory category, CancellationToken cancellationToken = default);

    Task<bool> IsOptedOutAsync(
        Guid userId, MessageCategory category, CancellationToken cancellationToken = default);

    Task<bool> AcceptsFacilitatedMessagesAsync(
        Guid userId, CancellationToken cancellationToken = default);

    /// <summary>The subset of <paramref name="userIds"/> with the inbox off. No row means inbox on.</summary>
    Task<IReadOnlySet<Guid>> GetUsersWithInboxDisabledAsync(
        IReadOnlyList<Guid> userIds, MessageCategory category,
        CancellationToken cancellationToken = default);

    /// <summary>Counts recorded rows only.</summary>
    Task<int> GetCountByCategoryAndStateAsync(
        MessageCategory category, bool optedOut, CancellationToken cancellationToken = default);
}

/// <summary>Writes made from outside Email (MailerLite's import). Every change is audited.</summary>
public interface ICommunicationPreferenceWrite : IApplicationService
{
    /// <summary>Sets the email opt-out and keeps the inbox setting. Idempotent.</summary>
    Task UpdatePreferenceAsync(
        Guid userId, MessageCategory category, bool optedOut, string source,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the category to "nothing recorded", which is different from an explicit opt-out.
    /// No-op when nothing is recorded.
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
