namespace Humans.Application.Interfaces.Events;

/// <summary>
/// T-03 — One-way cache-staleness signal for the Events section's cached
/// read-models (<see cref="ApprovedEventView"/>, <see cref="EventCategoryView"/>,
/// <see cref="EventVenueView"/>, <see cref="EventGuideSettingsView"/>).
/// Implemented by the Singleton caching decorator.
/// </summary>
/// <remarks>
/// <para>
/// All event_* table writes flow through <c>IEventService</c> by design
/// (enforced by the <c>Only_EventRepository_Writes_Event_DbSets</c>
/// architecture test), so the decorator handles its own invalidation
/// inline after each delegated write. This interface exists for the
/// one cross-section signal the section will eventually receive — see the
/// <see cref="EventGuideSettingsView"/> remarks for the stale-on-EventSettings
/// stop-gap tracked in
/// <see href="https://github.com/nobodies-collective/Humans/issues/719"/>.
/// External callers should not invoke these methods today; they are kept
/// non-public-API-surface until #719 wires the EventSettings invalidation
/// edge.
/// </para>
/// </remarks>
public interface IEventViewInvalidator
{
    /// <summary>
    /// Reloads the cached <see cref="EventGuideSettingsView"/> singleton
    /// (including the foreign-read <see cref="EventGuideSettingsView.TimeZoneId"/>
    /// from <c>EventSettings</c>). Used as the future #719 hook so an
    /// EventSettings edit can flush this section's TimeZoneId cache without
    /// the decorator owning the foreign table.
    /// </summary>
    Task InvalidateGuideSettingsAsync(CancellationToken ct = default);

    /// <summary>
    /// Drops every cached entry — approved events, categories, venues, and
    /// settings — and forces the next read to re-warm from the DB. Used by
    /// the rare wholesale-flip case (admin "reset event edition" workflow);
    /// not part of any production write path today.
    /// </summary>
    Task InvalidateAllAsync(CancellationToken ct = default);
}
