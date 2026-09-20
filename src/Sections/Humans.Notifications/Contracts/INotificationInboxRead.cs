using System.Security.Claims;
using Humans.Base.Interfaces;

namespace Humans.Notifications.Contracts;

/// <summary>
/// Notifications' read-only inbox surface for the machine API behind
/// <c>/api/backdoor/notifications</c>: one human's unread notifications plus the live
/// meters their roles unlock. Polling never marks a row read, dismisses it or resolves it.
/// </summary>
/// <remarks>
/// Takes the request principal rather than a user id because the meters are role-gated on
/// <c>ClaimTypes.Role</c> and the per-user ones read <c>ClaimTypes.NameIdentifier</c>; the
/// Backdoor filter installs both for the key's owner. Everything else the inbox does (popup,
/// resolve, dismiss, mark-read) stays internal.
/// </remarks>
public interface INotificationInboxRead : IApplicationService
{
    /// <summary>
    /// Unread rows newest first, then the meters visible to <paramref name="user"/> highest
    /// priority first. A principal without a user id gets no rows.
    /// </summary>
    Task<NotificationInboxSnapshot> GetUnreadInboxAsync(ClaimsPrincipal user, CancellationToken ct = default);
}

/// <summary>What one poll of the machine inbox returns.</summary>
public sealed record NotificationInboxSnapshot(
    IReadOnlyList<UnreadNotificationDto> Notifications,
    IReadOnlyList<NotificationMeterDto> Meters);

/// <summary>One unread, unresolved notification row.</summary>
/// <param name="ActionUrl">Where the row points, null when it is informational only.</param>
/// <param name="IsUnread">Always true today (the query is the unread tab); kept so a consumer need not infer it.</param>
public sealed record UnreadNotificationDto(
    DateTime CreatedAt,
    NotificationSource Source,
    string Title,
    string? ActionUrl,
    NotificationPriority Priority,
    NotificationClass Class,
    bool IsUnread);

/// <summary>One live work-queue count the principal's roles let them see.</summary>
public sealed record NotificationMeterDto(string Title, int Count, string ActionUrl);
