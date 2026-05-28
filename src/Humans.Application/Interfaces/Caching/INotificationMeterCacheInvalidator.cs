using Humans.Application.Architecture;

namespace Humans.Application.Interfaces.Caching;

/// <summary>
/// Cross-cutting invalidator for the notification meters cache.
/// See <see cref="INavBadgeCacheInvalidator"/> for the same rationale.
/// </summary>
[Grandfathered(
    ruleId: "HUM0028",
    justification: "Pre-existing nav-badge notification-meter cache; remains until the meter is absorbed into NotificationInboxService's caching decorator.",
    since: "2026-05-27",
    issueRef: "nobodies-collective/Humans#805")]
public interface INotificationMeterCacheInvalidator : IInvalidator
{
    void Invalidate();
}
