using Humans.Notifications.Contracts;
using NodaTime;

namespace Humans.Notifications.Domain;

/// <summary>Resolution is shared: when any recipient resolves, it resolves for all.</summary>
internal sealed class Notification
{
    internal const int TitleMaxLength = 200;
    internal const int BodyMaxLength = 2000;

    public Guid Id { get; init; }

    public string Title { get; set; } = string.Empty;

    public string? Body { get; set; }

    public string? ActionUrl { get; set; }

    /// <summary>Stored only: no page renders it and the export leaves it out.</summary>
    public string? ActionLabel { get; set; }

    public NotificationPriority Priority { get; set; }

    public NotificationSource Source { get; set; }

    /// <summary>The source entity (e.g. an issue id), so its section can auto-resolve via <c>ResolveBySourceKeyAsync</c>.</summary>
    public string? SourceKey { get; set; }

    public NotificationClass Class { get; set; }

    public string? TargetGroupName { get; set; }

    public Instant CreatedAt { get; init; }

    public Instant? ResolvedAt { get; set; }

    public Guid? ResolvedByUserId { get; set; }

    public ICollection<NotificationRecipient> Recipients { get; init; } = new List<NotificationRecipient>();
}
