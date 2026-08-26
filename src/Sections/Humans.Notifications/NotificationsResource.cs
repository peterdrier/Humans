namespace Humans.Notifications;

/// <summary>
/// Marker type for Notifications' resource set. The <c>.resx</c> files sit beside this
/// file on purpose: the SDK derives the manifest name from the adjacent same-named
/// <c>.cs</c> file's namespace, not from the folder path, so this must stay
/// <c>namespace Humans.Notifications</c> — <c>Humans.Notifications.Resources</c> would
/// make every notification string fall back to its raw key at runtime.
/// </summary>
/// <remarks>
/// Public because the boot localization diagnostic discovers section resource markers
/// via <c>GetExportedTypes()</c>; an internal marker is skipped in silence. The set
/// includes the keys the notification bell renders — the bell view component and its
/// <c>Default.cshtml</c> live in this section so the whole <c>Notification_*</c> set
/// stays in one resource set. Shell renders the bell by name, through
/// <c>Component.InvokeAsync("NotificationBell")</c>.
/// </remarks>
public class NotificationsResource;
