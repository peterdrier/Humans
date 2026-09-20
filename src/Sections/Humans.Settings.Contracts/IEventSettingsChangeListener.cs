namespace Humans.Settings.Contracts;

/// <summary>
/// Fan-out seam: a section implements this to be told the event settings row
/// changed, so it can flush whatever it caches off that row.
/// </summary>
/// <remarks>
/// The gate date, timezone, phase offsets and the active-event flip all move
/// derived dates for every member at once, and each consuming section caches
/// something built from them (early-entry dates, shift calendars). Before the
/// consolidation those writes lived in the consuming sections, which flushed
/// their own caches inline; the write moved to
/// <c>ISettingsWriteService.SaveEventSettingsAsync</c>, so the notification
/// moves with it. Settings enumerates <c>IEnumerable&lt;IEventSettingsChangeListener&gt;</c>
/// after a successful save rather than naming each consumer — same shape as
/// <c>IUserDataContributor</c> / <c>ICalendarFeedContributor</c>, and it keeps
/// Settings free of a project reference per consumer
/// (nobodies-collective/Humans#805, peterdrier/Humans#1627).
/// <para>Implementations are called inline on the write path and must not throw:
/// keep them to a cache flush.</para>
/// </remarks>
public interface IEventSettingsChangeListener
{
    /// <summary>The event settings row was written. Flush anything derived from it.</summary>
    void EventSettingsChanged();
}
