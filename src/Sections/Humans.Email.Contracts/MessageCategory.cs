namespace Humans.Email.Contracts;

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
    /// Categories where users cannot opt out — always locked on.
    /// </summary>
    public static bool IsAlwaysOn(this MessageCategory category) => category is
        MessageCategory.System or MessageCategory.CampaignCodes;

    /// <summary>
    /// Whether a category is opted-out when the user has no preference row.
    /// Marketing is the only opt-in-required category (GDPR); every other category
    /// defaults to on. Single source of truth consumed by
    /// <c>CommunicationPreferenceService</c> (default-row seeding +
    /// <c>IsOptedOutAsync</c> fallback) and the preferences UI (rendering a missing
    /// row) — so a category with no row reads the same everywhere.
    /// </summary>
    public static bool DefaultOptedOut(this MessageCategory category) =>
        category is MessageCategory.Marketing;

    /// <summary>
    /// The active categories shown in the Communication Preferences UI, in display order.
    /// </summary>
    public static IReadOnlyList<MessageCategory> ActiveCategories { get; } =
    [
        MessageCategory.System,
        MessageCategory.CampaignCodes,
        MessageCategory.FacilitatedMessages,
        MessageCategory.Ticketing,
        MessageCategory.VolunteerUpdates,
        MessageCategory.TeamUpdates,
        MessageCategory.Governance,
        MessageCategory.Marketing
    ];

}
