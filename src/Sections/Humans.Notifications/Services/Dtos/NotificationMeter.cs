namespace Humans.Notifications.Services.Dtos;

/// <summary>Computed per request, never stored; hidden at zero.</summary>
internal sealed class NotificationMeter
{
    public required string Title { get; init; }

    public required int Count { get; init; }

    public required string ActionUrl { get; init; }

    /// <summary>Higher first.</summary>
    public required int Priority { get; init; }
}
