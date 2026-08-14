namespace Humans.Users.Contracts;

/// <summary>
/// Types of contact information a member can share.
/// </summary>
public enum ContactFieldType
{
    /// <summary>
    /// Email address. Deprecated: use UserEmail entity for email management.
    /// Kept for safe deserialization of existing data.
    /// </summary>
    [Obsolete("Use UserEmail entity for email management. Kept for backward compatibility.")]
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
    /// Discord username.
    /// </summary>
    Discord = 5,

    /// <summary>
    /// Other contact method (requires CustomLabel).
    /// </summary>
    Other = 99
}
