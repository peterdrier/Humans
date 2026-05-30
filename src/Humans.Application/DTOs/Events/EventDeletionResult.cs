namespace Humans.Application.DTOs.Events;

/// <summary>
/// Three-way outcome of deleting a category or shared venue. Replaces the prior
/// <c>(bool deleted, int linkedCount)</c> tuple, whose <c>linkedCount == -1</c>
/// value silently doubled as a "not found" sentinel.
/// </summary>
public enum EventDeletionStatus
{
    /// <summary>The category/venue was deleted.</summary>
    Deleted,

    /// <summary>No category/venue exists with the given id.</summary>
    NotFound,

    /// <summary>Deletion was blocked because guide events still reference it.</summary>
    HasLinkedEvents
}

/// <summary>
/// Result of a category/venue delete attempt.
/// <see cref="LinkedEventCount"/> is only meaningful when
/// <see cref="Status"/> is <see cref="EventDeletionStatus.HasLinkedEvents"/>.
/// </summary>
public sealed record EventDeletionResult(EventDeletionStatus Status, int LinkedEventCount)
{
    public static readonly EventDeletionResult Deleted = new(EventDeletionStatus.Deleted, 0);
    public static readonly EventDeletionResult NotFound = new(EventDeletionStatus.NotFound, 0);
    public static EventDeletionResult Blocked(int linkedEventCount) =>
        new(EventDeletionStatus.HasLinkedEvents, linkedEventCount);
}
