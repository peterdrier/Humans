namespace Profiles.Domain.Enums;

/// <summary>
/// Types of contact information a member can share.
/// </summary>
public enum ContactFieldType
{
    /// <summary>
    /// Email address.
    /// </summary>
    Email = 0,

    /// <summary>
    /// Phone number.
    /// </summary>
    Phone = 1,

    /// <summary>
    /// Signal messenger.
    /// </summary>
    Signal = 2,

    /// <summary>
    /// Telegram messenger.
    /// </summary>
    Telegram = 3,

    /// <summary>
    /// WhatsApp messenger.
    /// </summary>
    WhatsApp = 4,

    /// <summary>
    /// Other contact method (requires CustomLabel).
    /// </summary>
    Other = 99
}
