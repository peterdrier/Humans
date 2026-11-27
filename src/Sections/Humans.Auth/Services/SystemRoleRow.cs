using NodaTime;

namespace Humans.Auth.Services;

/// <summary>The cached unit of <c>CachingSystemRoleService</c>.</summary>
internal sealed record SystemRoleRow(
    Guid Id,
    Guid UserId,
    string RoleName,
    Instant ValidFrom,
    Instant? ValidTo)
{
    /// <summary>The one active-at rule; the caching decorator uses it rather than restating it.</summary>
    public bool IsActiveAt(Instant now) =>
        ValidFrom <= now && (ValidTo is null || ValidTo > now);
}
