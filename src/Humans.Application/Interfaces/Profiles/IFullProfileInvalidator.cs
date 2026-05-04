using System.Runtime.CompilerServices;

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
public interface IFullProfileInvalidator
{
    /// <summary>
    /// Invalidate (reload) the cached <see cref="FullProfile"/> for
    /// <paramref name="userId"/>. The optional <paramref name="callerMember"/>
    /// and <paramref name="callerFile"/> parameters are auto-supplied via
    /// <see cref="CallerMemberNameAttribute"/> / <see cref="CallerFilePathAttribute"/>
    /// and are emitted to the log in non-Production environments — issue #635
    /// (§15i) makes this the canonical safety net for confirming every
    /// Profile-affecting write hits the invalidator.
    /// </summary>
    Task InvalidateAsync(
        Guid userId,
        CancellationToken ct = default,
        [CallerMemberName] string callerMember = "",
        [CallerFilePath] string callerFile = "");
}
