using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Interfaces.Stores;

/// <summary>
/// Denormalized cached representation of a <see cref="User"/> entity,
/// shaped for in-memory lookups. Contains only data derived from the User
/// entity itself — no cross-domain data (Profile, TeamMembership, etc.).
/// Cross-domain shapes are assembled at the service layer per
/// <c>docs/architecture/design-rules.md</c> §6 (in-memory joins).
/// </summary>
public record CachedUser(
    Guid Id,
    string? Email,
    string DisplayName,
    string PreferredLanguage,
    string? ProfilePictureUrl,
    string? GoogleEmail,
    GoogleEmailStatus GoogleEmailStatus,
    bool UnsubscribedFromCampaigns,
    bool SuppressScheduleChangeEmails,
    ContactSource? ContactSource,
    string? ExternalSourceId,
    Instant CreatedAt,
    Instant? LastLoginAt,
    Instant? DeletionRequestedAt,
    Instant? DeletionScheduledFor,
    Instant? DeletionEligibleAfter,
    Instant? LastConsentReminderSentAt,
    Instant? MagicLinkSentAt,
    Guid? ICalToken)
{
    public static CachedUser Create(User user) => new(
        Id: user.Id,
        Email: user.Email,
        DisplayName: user.DisplayName,
        PreferredLanguage: user.PreferredLanguage,
        ProfilePictureUrl: user.ProfilePictureUrl,
        GoogleEmail: user.GoogleEmail,
        GoogleEmailStatus: user.GoogleEmailStatus,
        UnsubscribedFromCampaigns: user.UnsubscribedFromCampaigns,
        SuppressScheduleChangeEmails: user.SuppressScheduleChangeEmails,
        ContactSource: user.ContactSource,
        ExternalSourceId: user.ExternalSourceId,
        CreatedAt: user.CreatedAt,
        LastLoginAt: user.LastLoginAt,
        DeletionRequestedAt: user.DeletionRequestedAt,
        DeletionScheduledFor: user.DeletionScheduledFor,
        DeletionEligibleAfter: user.DeletionEligibleAfter,
        LastConsentReminderSentAt: user.LastConsentReminderSentAt,
        MagicLinkSentAt: user.MagicLinkSentAt,
        ICalToken: user.ICalToken);
}

/// <summary>
/// In-memory canonical store for <see cref="CachedUser"/> entries,
/// keyed by <c>UserId</c> with a secondary case-insensitive index on
/// <see cref="CachedUser.Email"/>. See <c>docs/architecture/design-rules.md</c>
/// §4 for the store pattern.
/// </summary>
/// <remarks>
/// Warmed at startup via <c>IUserRepository.GetAllAsync()</c>; at ~500-user
/// scale the full set fits in memory trivially. Replaces inline
/// <c>IMemoryCache</c> lookups for user data.
///
/// <para>
/// <b>Single-writer invariant:</b> only <c>UserService</c> may call
/// <see cref="Upsert"/> or <see cref="Remove"/>, and always immediately
/// after a successful repository write. No other service, controller,
/// job, or view component writes to the store. The store keeps its
/// primary (<c>Id</c>) and secondary (email) indexes consistent
/// because there is exactly one writer.
/// </para>
/// </remarks>
public interface IUserStore
{
    /// <summary>
    /// O(1) primary lookup by <see cref="CachedUser.Id"/>.
    /// </summary>
    CachedUser? GetById(Guid userId);

    /// <summary>
    /// Case-insensitive lookup on <see cref="CachedUser.Email"/> (the
    /// <c>IdentityUser.Email</c>, NOT <see cref="CachedUser.GoogleEmail"/>).
    /// Returns <c>null</c> if the email is null, empty, or not indexed.
    /// </summary>
    CachedUser? GetByEmail(string email);

    /// <summary>
    /// Snapshot of all cached users in the store.
    /// </summary>
    IReadOnlyList<CachedUser> GetAll();

    /// <summary>
    /// Inserts or replaces a cached user. Updates both the primary id
    /// index and the secondary email index atomically — if the email
    /// changed relative to an existing entry, the old email index entry
    /// is removed before the new one is inserted.
    /// </summary>
    void Upsert(CachedUser user);

    /// <summary>
    /// Removes a cached user from both the primary id index and the
    /// secondary email index. No-op if not present.
    /// </summary>
    void Remove(Guid userId);

    /// <summary>
    /// Replaces the entire contents of the store. Used by the startup
    /// warmup hosted service to populate the store once from the
    /// repository.
    /// </summary>
    void LoadAll(IReadOnlyCollection<CachedUser> users);
}
