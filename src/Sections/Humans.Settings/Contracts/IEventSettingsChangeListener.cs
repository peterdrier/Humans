namespace Humans.Settings.Contracts;

/// <summary>
/// Told after every successful event-settings write, so a section can flush what it caches off the row. Called inline
/// on the write path: must not throw, so keep it to a cache flush.
/// </summary>
public interface IEventSettingsChangeListener
{
    /// <summary>
    /// Also called on delete. Listeners with per-event cache entries (Shifts' dashboard aggregates) need the id to
    /// evict the right ones; others may flush everything.
    /// </summary>
    void EventSettingsChanged(Guid eventSettingsId);
}
