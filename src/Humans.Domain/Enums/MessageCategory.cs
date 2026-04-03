namespace Humans.Domain.Enums;

/// <summary>
/// Categories of system communications for preference management.
/// Stored as string in DB; new values can be appended without migration.
/// </summary>
public enum MessageCategory
{
    /// <summary>
    /// Critical system messages (account, consent, security). Always on — cannot opt out.
    /// </summary>
    System = 0,

    /// <summary>
    /// Shift changes, schedule updates, team additions. Default: on.
    /// </summary>
    EventOperations = 1,

    /// <summary>
    /// General community news and facilitated messages. Default: off.
    /// </summary>
    CommunityUpdates = 2,

    /// <summary>
    /// Campaign emails, promotions. Default: off.
    /// </summary>
    Marketing = 3,

    /// <summary>
    /// Board voting, tier applications, role assignments, and onboarding reviews. Default: on.
    /// </summary>
    Governance = 4
}

public static class MessageCategoryExtensions
{
    public static string ToDisplayName(this MessageCategory category) => category switch
    {
        MessageCategory.System => "System",
        MessageCategory.EventOperations => "Event Operations",
        MessageCategory.CommunityUpdates => "Community Updates",
        MessageCategory.Marketing => "Marketing",
        MessageCategory.Governance => "Governance",
        _ => category.ToString(),
    };

    public static string ToDescription(this MessageCategory category) => category switch
    {
        MessageCategory.System => "Critical account, consent, and security notifications. Always on.",
        MessageCategory.EventOperations => "Shift changes, schedule updates, and team notifications.",
        MessageCategory.CommunityUpdates => "General community news and facilitated messages.",
        MessageCategory.Marketing => "Campaign emails and promotions.",
        MessageCategory.Governance => "Board voting, tier applications, role assignments, and onboarding reviews.",
        _ => string.Empty,
    };
}
