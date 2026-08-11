namespace Humans.Notifications;

/// <summary>
/// Marker type for Notifications' resource set. The <c>.resx</c> files sit beside this
/// file on purpose: the SDK derives the manifest name from the adjacent same-named
/// <c>.cs</c> file's namespace, not from the folder path, so this must stay
/// <c>namespace Humans.Notifications</c> — <c>Humans.Notifications.Resources</c> would
/// make every notification string fall back to its raw key at runtime (design §3).
/// </summary>
/// <remarks>
/// Public because the boot localization diagnostic discovers section resource markers
/// via <c>GetExportedTypes()</c>; an internal marker is skipped in silence (§15.3b).
/// The set includes the two keys the notification bell renders: the bell view component
/// and its <c>Default.cshtml</c> moved into this section so the whole
/// <c>Notification_*</c> set could come home rather than splitting across two resource
/// sets. Shell's layouts still render it — by name, through
/// <c>Component.InvokeAsync("NotificationBell")</c>.
/// </remarks>
public class NotificationsResource { }
