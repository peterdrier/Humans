using Humans.Domain.Entities;
using Humans.Domain.Enums;

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
    /// Persists changes to tracked entities. Used after in-place mutations on
    /// entities returned by the tracked query methods.
    /// </summary>
    Task SaveChangesAsync(CancellationToken ct = default);
}
