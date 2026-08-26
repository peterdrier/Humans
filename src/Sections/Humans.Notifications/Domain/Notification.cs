using Humans.Notifications.Contracts;
using NodaTime;

namespace Humans.Notifications.Domain;

/// <summary>
/// A notification dispatched to one or more recipients.
/// Resolution is shared: when any recipient resolves, it resolves for all.
/// </summary>
internal sealed class Notification
{
    public Guid Id { get; init; }

    public string Title { get; set; } = string.Empty;

    public string? Body { get; set; }

    public string? ActionUrl { get; set; }

    /// <summary>
    /// Optional button label text (e.g. "Review →", "Approve →", "Find cover →").
    /// Falls back to the localized default in the UI if null.
    /// </summary>
    public string? ActionLabel { get; set; }

    public NotificationPriority Priority { get; set; }

    public NotificationSource Source { get; set; }

    /// <summary>
    /// Optional correlation key identifying the specific source entity (e.g. the
    /// issue id behind an <see cref="NotificationSource.IssueSubmitted"/> alert).
    /// Lets the originating section auto-resolve this notification when that entity
    /// reaches a terminal state, via <c>ResolveBySourceKeyAsync(source, sourceKey)</c>.
    /// Null for notifications not tied to a single entity.
    /// </summary>
    public string? SourceKey { get; set; }

    public NotificationClass Class { get; set; }

    /// <summary>
    /// Display name for group-targeted notifications (e.g. "Coordinators", "Board").
    /// Null for individual-targeted notifications.
    /// </summary>
    public string? TargetGroupName { get; set; }

    public Instant CreatedAt { get; init; }

    /// <summary>Null = unresolved.</summary>
    public Instant? ResolvedAt { get; set; }

    /// <summary>
    /// Bare cross-section User id — no FK constraint, no navigation property.
    /// Display names are stitched in memory via <c>IUserServiceRead</c>.
    /// </summary>
    public Guid? ResolvedByUserId { get; set; }

    public ICollection<NotificationRecipient> Recipients { get; init; } = new List<NotificationRecipient>();
}
