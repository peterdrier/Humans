namespace Humans.Calendar.Domain;

/// <summary>Absent until the member creates a feed.</summary>
internal sealed class CalendarFeedToken
{
    public Guid UserId { get; set; }

    /// <summary>The secret. Replacing it revokes every URL handed out so far.</summary>
    public Guid Token { get; set; }
}
