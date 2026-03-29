using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Domain.Entities;

/// <summary>
/// Tracks a user's opt-in/opt-out preference for a specific message category.
/// One row per user per category. Used for CAN-SPAM/GDPR compliance.
/// </summary>
public class CommunicationPreference
{
    public Guid Id { get; init; }

    public Guid UserId { get; init; }

    /// <summary>
    /// The message category this preference applies to.
    /// </summary>
    public MessageCategory Category { get; init; }

    /// <summary>
    /// True if the user has opted out of this category.
    /// </summary>
    public bool OptedOut { get; set; }

    /// <summary>
    /// When this preference was last changed.
    /// </summary>
    public Instant UpdatedAt { get; set; }

    /// <summary>
    /// How this preference was set: "Profile", "MagicLink", "DataMigration", etc.
    /// </summary>
    public string UpdateSource { get; set; } = string.Empty;

    /// <summary>
    /// Navigation to the user.
    /// </summary>
    public User User { get; init; } = null!;
}
