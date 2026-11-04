namespace Humans.Notifications.Services.Dtos;

/// <summary>Computed per request, never stored; hidden at zero.</summary>
internal sealed class NotificationMeter
{
    public required string Title { get; init; }

    public required int Count { get; init; }

    public required string ActionUrl { get; init; }

    /// <summary>Who the count is shown to, so the reader knows why they see it.</summary>
    public required string Audience { get; init; }

    /// <summary>Higher first.</summary>
    public required int Priority { get; init; }
}
