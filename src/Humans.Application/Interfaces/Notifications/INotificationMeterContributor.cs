using System.Security.Claims;
using Humans.Application.DTOs;

namespace Humans.Application.Interfaces.Notifications;

/// <summary>
/// Cache scope for a notification meter count.
/// </summary>
public enum NotificationMeterScope
{
    /// <summary>
    /// Count is a cross-user aggregate (e.g. total applications pending). Shared
    /// across all users; visibility filtered by role.
    /// </summary>
    Global,

    /// <summary>
    /// Count depends on the identity of the viewing user (e.g. applications this
    /// board member has not yet voted on).
    /// </summary>
    PerUser,
}

/// <summary>
/// A single notification meter contributed by an owning section. Sections register
/// one contributor per meter via their DI extension; <see cref="INotificationMeterProvider"/>
/// aggregates them into the navbar badge list with no section knowledge of its own.
/// </summary>
/// <remarks>
/// <para>
/// This is the push-model replacement for the pre-§15 pull model where
/// <see cref="INotificationMeterProvider"/> knew every section explicitly. Each section
/// owns its meter: label, icon/action URL, priority, role visibility, and the count
/// query (routed through the section's own service — no direct DB access).
/// </para>
/// <para>
/// Global-scoped contributors must compute a user-agnostic count. The provider caches
/// global meter results in a cross-user bundle (2-minute TTL) under
/// <c>CacheKeys.NotificationMeters</c>, and the <see cref="user"/> argument to
/// <see cref="BuildMeterAsync"/> is a sentinel empty principal for these. Per-user
/// contributors receive the real principal and are invoked on every request — they
/// self-cache if caching is desired.
/// </para>
/// </remarks>
public interface INotificationMeterContributor
{
    /// <summary>
    /// Stable identifier used in the global-meter cache dictionary and in diagnostic
    /// logs. Must be unique across all registered contributors.
    /// </summary>
    string Key { get; }

    /// <summary>
    /// Whether this meter's count is a cross-user aggregate (<see cref="NotificationMeterScope.Global"/>)
    /// or per-user (<see cref="NotificationMeterScope.PerUser"/>).
    /// </summary>
    NotificationMeterScope Scope { get; }

    /// <summary>
    /// Cheap synchronous role gate. Evaluated before <see cref="BuildMeterAsync"/> so
    /// count queries are skipped entirely for users who would not see the meter.
    /// </summary>
    bool IsVisibleTo(ClaimsPrincipal user);

    /// <summary>
    /// Computes the meter. Returns <see langword="null"/> when the count is zero or
    /// there is otherwise nothing to display. Only invoked when
    /// <see cref="IsVisibleTo"/> returned <see langword="true"/>.
    /// </summary>
    /// <param name="user">
    /// The viewing user for <see cref="NotificationMeterScope.PerUser"/>-scoped contributors.
    /// For <see cref="NotificationMeterScope.Global"/> contributors this is a sentinel
    /// empty principal — global contributors must produce a user-agnostic count.
    /// </param>
    Task<NotificationMeter?> BuildMeterAsync(ClaimsPrincipal user, CancellationToken cancellationToken);
}
