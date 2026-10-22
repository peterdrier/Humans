namespace Humans.Notifications.Contracts;

public enum NotificationClass
{
    /// <summary>Dismissable; suppressed by the recipient's InboxEnabled preference.</summary>
    Informational = 0,

    /// <summary>Never suppressed or dismissed; only resolved.</summary>
    Actionable = 1
}
