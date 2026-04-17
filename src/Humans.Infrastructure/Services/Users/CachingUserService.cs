using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Stores;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Infrastructure.Services.Users;

/// <summary>
/// Caching decorator for <see cref="IUserService"/>. Currently a thin
/// pass-through: reads that return full <see cref="User"/> entities cannot be
/// trivially rehydrated from <see cref="CachedUser"/> (User is an
/// <c>IdentityUser&lt;Guid&gt;</c> with many framework fields), so they
/// delegate to the inner service. Writes delegate to the inner service, which
/// refreshes <see cref="IUserStore"/> itself.
/// </summary>
/// <remarks>
/// <para>
/// Even as a pass-through, the decorator serves two purposes:
/// </para>
/// <list type="number">
///   <item>Completes the symmetrical repository/store/decorator pattern for the
///   service-ownership migration (see <c>docs/architecture/design-rules.md</c> §5).</item>
///   <item>Provides a clean extension point: callers that only need display
///   data (DisplayName, Email, GoogleEmail, …) can migrate to
///   <see cref="IUserStore.GetById"/> directly in a follow-up, bypassing this
///   decorator entirely.</item>
/// </list>
/// </remarks>
public sealed class CachingUserService : IUserService
{
    private readonly IUserService _inner;
    private readonly IUserStore _store;

    public CachingUserService(IUserService inner, IUserStore store)
    {
        _inner = inner;
        _store = store;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Reads — all pass through to the inner service
    // ──────────────────────────────────────────────────────────────────────────

    public Task<User?> GetByIdAsync(Guid userId, CancellationToken ct = default) =>
        _inner.GetByIdAsync(userId, ct);

    public Task<IReadOnlyDictionary<Guid, User>> GetByIdsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default) =>
        _inner.GetByIdsAsync(userIds, ct);

    public Task<IReadOnlyList<User>> GetAllUsersAsync(CancellationToken ct = default) =>
        _inner.GetAllUsersAsync(ct);

    public Task<User?> GetByEmailOrAlternateAsync(string email, CancellationToken ct = default) =>
        _inner.GetByEmailOrAlternateAsync(email, ct);

    public Task<IReadOnlyList<User>> GetContactUsersAsync(string? search, CancellationToken ct = default) =>
        _inner.GetContactUsersAsync(search, ct);

    public Task<EventParticipation?> GetParticipationAsync(Guid userId, int year, CancellationToken ct = default) =>
        _inner.GetParticipationAsync(userId, year, ct);

    public Task<List<EventParticipation>> GetAllParticipationsForYearAsync(int year, CancellationToken ct = default) =>
        _inner.GetAllParticipationsForYearAsync(year, ct);

    // ──────────────────────────────────────────────────────────────────────────
    // Writes — pass through; inner service owns IUserStore upserts
    // ──────────────────────────────────────────────────────────────────────────

    public Task<bool> TrySetGoogleEmailAsync(Guid userId, string email, CancellationToken ct = default) =>
        _inner.TrySetGoogleEmailAsync(userId, email, ct);

    public Task UpdateDisplayNameAsync(Guid userId, string displayName, CancellationToken ct = default) =>
        _inner.UpdateDisplayNameAsync(userId, displayName, ct);

    public Task<bool> SetDeletionPendingAsync(Guid userId, Instant requestedAt, Instant scheduledFor, CancellationToken ct = default) =>
        _inner.SetDeletionPendingAsync(userId, requestedAt, scheduledFor, ct);

    public Task<bool> ClearDeletionAsync(Guid userId, CancellationToken ct = default) =>
        _inner.ClearDeletionAsync(userId, ct);

    public Task<EventParticipation> DeclareNotAttendingAsync(Guid userId, int year, CancellationToken ct = default) =>
        _inner.DeclareNotAttendingAsync(userId, year, ct);

    public Task<bool> UndoNotAttendingAsync(Guid userId, int year, CancellationToken ct = default) =>
        _inner.UndoNotAttendingAsync(userId, year, ct);

    public Task SetParticipationFromTicketSyncAsync(Guid userId, int year, ParticipationStatus status, CancellationToken ct = default) =>
        _inner.SetParticipationFromTicketSyncAsync(userId, year, status, ct);

    public Task RemoveTicketSyncParticipationAsync(Guid userId, int year, CancellationToken ct = default) =>
        _inner.RemoveTicketSyncParticipationAsync(userId, year, ct);

    public Task<int> BackfillParticipationsAsync(int year, List<(Guid UserId, ParticipationStatus Status)> entries, CancellationToken ct = default) =>
        _inner.BackfillParticipationsAsync(year, entries, ct);
}
