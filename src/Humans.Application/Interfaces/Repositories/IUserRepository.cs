using Humans.Domain.Entities;
using NodaTime;

namespace Humans.Application.Interfaces.Repositories;

/// <summary>
/// Repository for the <c>AspNetUsers</c> (User) and <c>event_participations</c>
/// tables. The only non-test file that may read or write those DbSets after
/// the User migration lands.
/// </summary>
/// <remarks>
/// Scope follows §8 of the design rules: this repository owns the User domain's
/// persistence — it does not navigate into Profile, Team, or other domains.
/// Cross-domain data is resolved by the caller via the owning service.
/// ASP.NET Identity's <c>UserManager&lt;User&gt;</c> / <c>SignInManager&lt;User&gt;</c>
/// are framework concerns and remain untouched by this migration.
/// </remarks>
public interface IUserRepository
{
    // ---- User lookups ----

    /// <summary>
    /// Loads a single user by id for mutation. Returns a tracked entity
    /// suitable for in-place modification followed by a repository mutation
    /// method that invokes <c>SaveChangesAsync</c>.
    /// </summary>
    Task<User?> GetByIdAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Loads a read-only single user by id. Used by decorator warmups,
    /// GDPR export, and any caller that will not mutate the entity.
    /// </summary>
    Task<User?> GetByIdReadOnlyAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Batched user fetch keyed by id. Missing users are absent from the
    /// returned dictionary. Read-only (AsNoTracking).
    /// </summary>
    Task<IReadOnlyDictionary<Guid, User>> GetByIdsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);

    /// <summary>
    /// Loads every user. Used by the startup warmup hosted service to populate
    /// <c>IUserStore</c>. Trivial at ~500-user scale. Read-only (AsNoTracking).
    /// </summary>
    Task<IReadOnlyList<User>> GetAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Finds a user by email, also matching against <c>GoogleEmail</c> and the
    /// gmail/googlemail alternate form. Case-insensitive via
    /// <c>EF.Functions.ILike</c>. Read-only (AsNoTracking).
    /// </summary>
    Task<User?> GetByEmailOrAlternateAsync(string email, CancellationToken ct = default);

    /// <summary>
    /// Returns all imported contact users — those with a non-null
    /// <c>ContactSource</c> who have never logged in — optionally filtered by
    /// a case-insensitive substring match on <c>DisplayName</c> or
    /// <c>Email</c>, ordered by <c>CreatedAt</c> descending. Read-only.
    /// </summary>
    Task<IReadOnlyList<User>> GetContactUsersAsync(string? search, CancellationToken ct = default);

    // ---- User mutations ----

    /// <summary>
    /// Sets <see cref="User.GoogleEmail"/> on the given user if it is currently
    /// null. Returns <c>true</c> when the value was set, <c>false</c> if the
    /// user does not exist or already has a GoogleEmail.
    /// </summary>
    Task<bool> TrySetGoogleEmailAsync(Guid userId, string email, CancellationToken ct = default);

    /// <summary>
    /// Updates <see cref="User.DisplayName"/>. No-op if the user does not exist.
    /// </summary>
    Task UpdateDisplayNameAsync(Guid userId, string displayName, CancellationToken ct = default);

    /// <summary>
    /// Marks a pending account deletion by setting <c>DeletionRequestedAt</c>
    /// and <c>DeletionScheduledFor</c>. Returns <c>false</c> if the user does
    /// not exist.
    /// </summary>
    Task<bool> SetDeletionPendingAsync(Guid userId, Instant requestedAt, Instant scheduledFor, CancellationToken ct = default);

    /// <summary>
    /// Clears <c>DeletionRequestedAt</c>, <c>DeletionScheduledFor</c>, and
    /// <c>DeletionEligibleAfter</c>. Returns <c>false</c> if the user does not
    /// exist.
    /// </summary>
    Task<bool> ClearDeletionAsync(Guid userId, CancellationToken ct = default);

    // ---- EventParticipation ----

    /// <summary>
    /// Loads the tracked participation record for a user and event year, or
    /// null if none exists. Tracked because mutation flows depend on it.
    /// </summary>
    Task<EventParticipation?> GetParticipationAsync(Guid userId, int year, CancellationToken ct = default);

    /// <summary>
    /// Loads all participation records for a given event year. Tracked so the
    /// caller can mutate rows in batch flows.
    /// </summary>
    Task<List<EventParticipation>> GetAllParticipationsForYearAsync(int year, CancellationToken ct = default);

    /// <summary>
    /// Loads every participation record for a given event year keyed by
    /// <c>UserId</c>. Used by the admin backfill bulk upsert flow. Tracked,
    /// to support in-place mutation before a single <see cref="SaveChangesAsync"/>.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, EventParticipation>> GetParticipationsForYearByUserIdAsync(
        int year, CancellationToken ct = default);

    /// <summary>
    /// Tracks a new <see cref="EventParticipation"/> for insertion. Does NOT
    /// call <c>SaveChangesAsync</c> — the caller is responsible for batching
    /// via <see cref="SaveChangesAsync"/>.
    /// </summary>
    Task AddParticipationAsync(EventParticipation entry, CancellationToken ct = default);

    /// <summary>
    /// Marks the given <see cref="EventParticipation"/> for removal. Does NOT
    /// call <c>SaveChangesAsync</c> — the caller is responsible for batching
    /// via <see cref="SaveChangesAsync"/>.
    /// </summary>
    Task RemoveParticipationAsync(EventParticipation entry, CancellationToken ct = default);

    /// <summary>
    /// Persists all tracked changes in a single <c>SaveChangesAsync</c> call.
    /// Used by service flows that add, modify, or remove one or more
    /// participation records in the same unit of work.
    /// </summary>
    Task SaveChangesAsync(CancellationToken ct = default);
}
