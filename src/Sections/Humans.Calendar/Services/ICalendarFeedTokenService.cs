using Humans.Base.Interfaces;

namespace Humans.Calendar.Services;

/// <summary>
/// Owns the credential behind the personal iCal feed URL: mint-on-first-view,
/// rotation from the feed card, and the GDPR/merge lifecycles. The whole lifecycle
/// sits in Calendar because the table does, so no page and no other section writes
/// it and there is no cross-section write to declare.
/// </summary>
internal interface ICalendarFeedTokenService : IApplicationService
{
    /// <summary>
    /// The member's token, or <c>null</c> when they have never opened the feed card.
    /// Mints nothing — the feed endpoint and the admin widget must not create a
    /// credential as a side effect of being looked at.
    /// </summary>
    Task<Guid?> GetAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// The member's token, minting one on first call. Only the page that renders the
    /// member their own URL calls this.
    /// </summary>
    Task<Guid> EnsureAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Replaces the member's token and returns the new one, revoking every URL
    /// handed out under the old one.
    /// </summary>
    Task<Guid> RotateAsync(Guid userId, CancellationToken ct = default);
}
