using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Interfaces.Repositories;

/// <summary>
/// Repository for the <c>communication_preferences</c> table.
/// The only non-test file that may write to this DbSet.
/// </summary>
public interface ICommunicationPreferenceRepository
{
    /// <summary>
    /// Returns all preferences for a user, tracked for modification.
    /// </summary>
    Task<List<CommunicationPreference>> GetByUserIdAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns a single preference by user and category, tracked.
    /// </summary>
    Task<CommunicationPreference?> GetByUserAndCategoryAsync(
        Guid userId, MessageCategory category, CancellationToken ct = default);

    /// <summary>
    /// Returns user ids from the input list that have inbox disabled
    /// for the given category.
    /// </summary>
    Task<IReadOnlySet<Guid>> GetUsersWithInboxDisabledAsync(
        IReadOnlyList<Guid> userIds, MessageCategory category,
        CancellationToken ct = default);

    /// <summary>
    /// Returns whether a user has any preference rows at all.
    /// </summary>
    Task<bool> HasAnyAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns user ids from the input list that have any preference rows.
    /// </summary>
    Task<IReadOnlySet<Guid>> GetUsersWithAnyPreferencesAsync(
        IReadOnlyList<Guid> userIds, CancellationToken ct = default);

    /// <summary>
    /// Returns all preferences for a user, read-only. Used by GDPR export.
    /// </summary>
    Task<IReadOnlyList<CommunicationPreference>> GetByUserIdReadOnlyAsync(
        Guid userId, CancellationToken ct = default);

    Task AddAsync(CommunicationPreference preference, CancellationToken ct = default);
    Task AddRangeAsync(IReadOnlyList<CommunicationPreference> preferences, CancellationToken ct = default);

    /// <summary>
    /// Attempts to insert <paramref name="defaults"/> for <paramref name="userId"/>.
    /// If another request races and inserts first (DbUpdateException), clears the
    /// change tracker and reloads from the database. Returns the final list.
    /// </summary>
    Task<List<CommunicationPreference>> AddDefaultsOrReloadAsync(
        Guid userId, IReadOnlyList<CommunicationPreference> defaults, CancellationToken ct = default);

    /// <summary>
    /// Persists changes to a single tracked <see cref="CommunicationPreference"/> entity.
    /// The caller must have obtained the entity via a tracked query method in the same scope.
    /// </summary>
    Task UpdateAsync(CommunicationPreference preference, CancellationToken ct = default);

    /// <summary>
    /// Bulk-moves <c>communication_preferences</c> rows from
    /// <paramref name="sourceUserId"/> to <paramref name="targetUserId"/> for the
    /// account-merge fold flow. Conflict rule per the fold spec: when source and
    /// target both have a row for the same <see cref="MessageCategory"/>, the
    /// rows collapse — the row with the most-recent <c>UpdatedAt</c> wins. If
    /// the source row is newer, its <c>OptedOut</c>, <c>InboxEnabled</c>, and
    /// <c>UpdateSource</c> values are copied onto the target row; the source
    /// row is then deleted. If the target row is at least as recent, the source
    /// row is simply deleted. Surviving source rows (no target row for the
    /// category) are re-FK'd to target. <c>UpdatedAt</c> is stamped to
    /// <paramref name="updatedAt"/> on every row touched. Returns the count of
    /// <c>communication_preferences</c> rows ultimately attributed to
    /// <paramref name="targetUserId"/>.
    /// </summary>
    Task<int> ReassignToUserAsync(
        Guid sourceUserId, Guid targetUserId, Instant updatedAt,
        CancellationToken ct = default);
}
