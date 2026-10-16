using Humans.Base.Attributes;
using Humans.Events.Contracts;
using Humans.Events.Services.Dtos;
using Humans.Base.Interfaces;

namespace Humans.Events.Services;

/// <summary>
/// One-way cache-staleness signal for the Events section's cached
/// read-models (<see cref="ApprovedEventView"/>, <see cref="EventCategoryView"/>,
/// <see cref="EventVenueView"/>, <see cref="EventGuideSettingsView"/>).
/// Implemented by the Singleton caching decorator.
/// </summary>
/// <remarks>
/// <para>
/// All event_* table writes flow through <c>IEventService</c> by design
/// (enforced by the universal <c>HUM0025</c> analyzer), so the decorator handles its own invalidation
/// inline after each delegated write. This interface is
/// a reserved in-section invalidation seat. The Settings-side signal arrives through
/// <c>IEventSettingsChangeListener</c>, not this interface; nothing calls
/// <c>InvalidateGuideSettingsAsync</c>, and external callers should not.
/// </para>
/// </remarks>
[Grandfathered(
    ruleId: "HUM0028",
    justification: "Pre-existing event-view cache flushed cross-section; remains until EventService's caching decorator owns invalidation end-to-end.",
    since: "2026-05-27",
    issueRef: "nobodies-collective/Humans#805")]
internal interface IEventViewInvalidator : IInvalidator
{
    /// <summary>
    /// Reloads the cached <see cref="EventGuideSettingsView"/> singleton
    /// (including the foreign-read <see cref="EventGuideSettingsView.TimeZoneId"/>
    /// from <c>EventSettings</c>). Reserved in-section seat with no caller; the Settings-side
    /// signal arrives through <c>IEventSettingsChangeListener</c>.
    /// </summary>
    Task InvalidateGuideSettingsAsync(CancellationToken ct = default);
}
