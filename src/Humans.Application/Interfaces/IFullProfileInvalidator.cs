namespace Humans.Application.Interfaces;

/// <summary>
/// One-way cache-staleness signal for <see cref="FullProfile"/>. Implemented
/// by the caching decorator in Infrastructure. External sections that change
/// user state in ways that affect Profile's cached view inject this and call
/// <see cref="InvalidateAsync"/> after their own writes — they never mutate
/// the cache directly. Invalidation reloads the entry from repositories,
/// preserving the fully-warm invariant (removes only when the user's profile
/// no longer exists).
/// </summary>
public interface IFullProfileInvalidator
{
    Task InvalidateAsync(Guid userId, CancellationToken ct = default);
}
