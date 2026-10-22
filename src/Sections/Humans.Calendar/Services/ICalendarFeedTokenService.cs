using Humans.Base.Interfaces;

namespace Humans.Calendar.Services;

internal interface ICalendarFeedTokenService : IApplicationService
{
    /// <summary>
    /// Null when the member has none. Never mints: looking at the feed or the admin widget must not create a credential.
    /// </summary>
    Task<Guid?> GetAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Mints on first call. Only the explicit create-feed POST calls this; no GET mints.</summary>
    Task<Guid> EnsureAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Revokes every URL handed out under the old token.</summary>
    Task<Guid> RotateAsync(Guid userId, CancellationToken ct = default);
}
