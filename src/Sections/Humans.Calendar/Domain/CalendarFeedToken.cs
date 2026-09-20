namespace Humans.Calendar.Domain;

/// <summary>
/// The credential behind a member's personal iCal subscription URL
/// (<c>/api/ical/{userId}/{token}.ics</c>). One row per member, created the first
/// time they open the feed card on <c>/Calendar</c> and absent until then.
/// </summary>
/// <remarks>
/// Calendar's data, not Users'. The token authenticates this section's feed and
/// nothing else; it lived on <c>User.ICalToken</c> only because the card originally
/// shipped inside Shifts' page, which already had a <c>User</c> to hand.
/// </remarks>
internal sealed class CalendarFeedToken
{
    /// <summary>The member the feed belongs to. Primary key — one token each.</summary>
    public Guid UserId { get; set; }

    /// <summary>The secret. Replacing it revokes every URL handed out so far.</summary>
    public Guid Token { get; set; }
}
