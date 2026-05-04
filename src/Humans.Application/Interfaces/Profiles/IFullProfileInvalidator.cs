namespace Humans.Application.Interfaces.Profiles;

/// <summary>
/// One-way cache-staleness signal for <see cref="FullProfile"/>. Implemented
/// by the caching decorator in Infrastructure. External sections that change
/// user state in ways that affect Profile's cached view inject this and call
/// <see cref="InvalidateAsync"/> after their own writes — they never mutate
/// the cache directly. Invalidation reloads the entry from repositories,
/// preserving the fully-warm invariant (removes only when the user's profile
/// no longer exists).
/// </summary>
/// <remarks>
/// Issue #635 (§15i): in non-Production environments the implementation logs
/// the calling member + file via a stack-walk so every Profile-affecting
/// write that hits the invalidator is visible during preview-environment
/// exploratory testing. The interface signature stays narrow so test mocks
/// don't have to pre-fill caller-info params.
/// </remarks>
public interface IFullProfileInvalidator
{
    Task InvalidateAsync(Guid userId, CancellationToken ct = default);
}
