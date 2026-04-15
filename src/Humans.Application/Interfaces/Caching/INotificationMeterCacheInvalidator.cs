namespace Humans.Application.Interfaces.Caching;

/// <summary>
/// Cross-cutting invalidator for the notification meters cache.
/// See <see cref="INavBadgeCacheInvalidator"/> for the same rationale.
/// </summary>
public interface INotificationMeterCacheInvalidator
{
    void Invalidate();
}
