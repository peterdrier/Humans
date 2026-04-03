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
    /// DEPRECATED — replaced by VolunteerUpdates + TeamUpdates. Kept for DB string compatibility.
    /// </summary>
    EventOperations = 1,

    /// <summary>
    /// DEPRECATED — replaced by FacilitatedMessages. Kept for DB string compatibility.
    /// </summary>
    CommunityUpdates = 2,

    /// <summary>
    /// Mailing list, promotions. Default: off.
    /// </summary>
    Marketing = 3,

    /// <summary>
    /// Board voting, tier applications, role assignments, and onboarding reviews. Default: on.
    /// </summary>
    Governance = 4,

    /// <summary>
    /// Discount codes, grants. Always on — cannot opt out.
    /// </summary>
    CampaignCodes = 5,

    /// <summary>
    /// User-to-user email via Humans. Default: on.
    /// </summary>
    FacilitatedMessages = 6,

    /// <summary>
    /// Purchase confirmations, event info. Default: on. Locked on when user has a matched ticket order.
    /// </summary>
    Ticketing = 7,

    /// <summary>
    /// Shift changes, schedule updates. Default: on.
    /// </summary>
    VolunteerUpdates = 8,

    /// <summary>
    /// Drive permissions, team member adds/removes. Default: on.
    /// </summary>
    TeamUpdates = 9,
}

public static class MessageCategoryExtensions
{
    /// <summary>
    /// Categories that are deprecated and should not appear in the UI.
    /// Kept in the enum for DB string compatibility only.
    /// </summary>
    public static bool IsDeprecated(this MessageCategory category) => category is
        MessageCategory.EventOperations or MessageCategory.CommunityUpdates;

    /// <summary>
    /// Categories where users cannot opt out — always locked on.
    /// </summary>
    public static bool IsAlwaysOn(this MessageCategory category) => category is
        MessageCategory.System or MessageCategory.CampaignCodes;

    /// <summary>
    /// The active categories shown in the Communication Preferences UI, in display order.
    /// </summary>
    public static IReadOnlyList<MessageCategory> ActiveCategories { get; } = new[]
    {
        MessageCategory.System,
        MessageCategory.CampaignCodes,
        MessageCategory.FacilitatedMessages,
        MessageCategory.Ticketing,
        MessageCategory.VolunteerUpdates,
        MessageCategory.TeamUpdates,
        MessageCategory.Governance,
        MessageCategory.Marketing,
    };

    public static string ToDisplayName(this MessageCategory category) => category switch
    {
        MessageCategory.System => "System",
        MessageCategory.EventOperations => "Event Operations",
        MessageCategory.CommunityUpdates => "Community Updates",
        MessageCategory.Marketing => "Marketing",
        MessageCategory.Governance => "Governance",
        MessageCategory.CampaignCodes => "Campaign Codes",
        MessageCategory.FacilitatedMessages => "Facilitated Messages",
        MessageCategory.Ticketing => "Ticketing",
        MessageCategory.VolunteerUpdates => "Volunteer Updates",
        MessageCategory.TeamUpdates => "Team Updates",
        _ => category.ToString(),
    };

    public static string ToDescription(this MessageCategory category) => category switch
    {
        MessageCategory.System => "Critical account, consent, and security notifications. Always on.",
        MessageCategory.EventOperations => "Shift changes, schedule updates, and team notifications.",
        MessageCategory.CommunityUpdates => "General community news and facilitated messages.",
        MessageCategory.Marketing => "Mailing list and promotions.",
        MessageCategory.Governance => "Board voting, tier applications, role assignments, and onboarding reviews.",
        MessageCategory.CampaignCodes => "Discount codes, grants, and campaign redemption codes. Always on.",
        MessageCategory.FacilitatedMessages => "Messages sent to you by other humans via Humans.",
        MessageCategory.Ticketing => "Purchase confirmations and event information.",
        MessageCategory.VolunteerUpdates => "Shift changes, schedule updates, and volunteer notifications.",
        MessageCategory.TeamUpdates => "Drive permissions, team member additions, and removals.",
        _ => string.Empty,
    };
}
