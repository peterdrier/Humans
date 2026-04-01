namespace Humans.Domain.Enums;

/// <summary>
/// Classification of a notification that determines its behavior.
/// </summary>
public enum NotificationClass
{
    /// <summary>
    /// Informational: dismissable, suppressible via InboxEnabled preference.
    /// </summary>
    Informational = 0,

    /// <summary>
    /// Actionable: requires action, always shown regardless of preferences.
    /// </summary>
    Actionable = 1
}
