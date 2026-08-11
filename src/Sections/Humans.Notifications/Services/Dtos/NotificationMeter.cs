namespace Humans.Notifications.Services.Dtos;

/// <summary>
/// A live counter notification representing pending work in a queue.
/// Computed at query time, not stored. Disappears when count reaches 0.
/// </summary>
internal sealed class NotificationMeter
{
    /// <summary>Display title for the meter (e.g. "Consent reviews pending").</summary>
    public required string Title { get; init; }

    /// <summary>Current pending count.</summary>
    public required int Count { get; init; }

    /// <summary>URL to the work queue where the count can be resolved.</summary>
    public required string ActionUrl { get; init; }

    /// <summary>Priority for visual ordering. Higher priority meters appear first.</summary>
    public required int Priority { get; init; }
}
