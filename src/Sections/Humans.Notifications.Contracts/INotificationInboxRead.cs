using System.Security.Claims;
using Humans.Base.Interfaces;

namespace Humans.Notifications.Contracts;

/// <summary>For <c>/api/backdoor/notifications</c>. Polling never marks a row read, dismisses or resolves it.</summary>
/// <remarks>Takes the principal, not a user id: the meters are gated on its role and name-identifier claims.</remarks>
public interface INotificationInboxRead : IApplicationService
{
    /// <summary>
    /// Unread rows newest first, then visible meters highest priority first. A principal without a user id gets
    /// no rows.
    /// </summary>
    Task<NotificationInboxSnapshot> GetUnreadInboxAsync(ClaimsPrincipal user, CancellationToken ct = default);
}

public sealed record NotificationInboxSnapshot(
    IReadOnlyList<UnreadNotificationDto> Notifications,
    IReadOnlyList<NotificationMeterDto> Meters);

/// <summary><paramref name="IsUnread"/> is always true today; kept so a consumer need not infer it.</summary>
public sealed record UnreadNotificationDto(
    DateTime CreatedAt,
    string Source,
    string Title,
    string? ActionUrl,
    NotificationPriority Priority,
    NotificationClass Class,
    bool IsUnread);

public sealed record NotificationMeterDto(string Title, int Count, string ActionUrl);
