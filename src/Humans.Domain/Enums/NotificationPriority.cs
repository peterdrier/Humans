namespace Humans.Domain.Enums;

/// <summary>
/// Priority level of a notification. Affects visual presentation (border accent, badge).
/// </summary>
public enum NotificationPriority
{
    /// <summary>Normal priority — default for most notifications.</summary>
    Normal = 0,

    /// <summary>High priority — more prominent visual treatment.</summary>
    High = 1,

    /// <summary>Critical priority — urgent, danger-styled visual treatment.</summary>
    Critical = 2
}
