namespace Humans.Users.Contracts;

/// <summary>Stored by name: never rename.</summary>
public enum ContactFieldType
{
    /// <summary>Retired: addresses live in <c>UserEmail</c>. Kept so stored rows still deserialize.</summary>
    [Obsolete("Use UserEmail entity for email management. Kept for backward compatibility.")]
    Email = 0,

    Phone = 1,

    Signal = 2,

    Telegram = 3,

    WhatsApp = 4,

    Discord = 5,

    /// <summary>Requires a custom label.</summary>
    Other = 99
}
