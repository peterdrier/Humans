namespace Humans.Email.Contracts;

/// <summary>Stored by name: never rename a value. Deprecated values stay for the rows that hold them.</summary>
public enum MessageCategory
{
    /// <summary>Account, consent and security. Always on.</summary>
    System = 0,

    /// <summary>Deprecated: replaced by <see cref="VolunteerUpdates"/> and <see cref="TeamUpdates"/>.</summary>
    EventOperations = 1,

    /// <summary>Deprecated: replaced by <see cref="FacilitatedMessages"/>.</summary>
    CommunityUpdates = 2,

    /// <summary>Mailing list and promotions. Default off.</summary>
    Marketing = 3,

    /// <summary>Board voting, tier applications, role assignments, onboarding reviews. Default on.</summary>
    Governance = 4,

    /// <summary>Discount codes and grants. Always on.</summary>
    CampaignCodes = 5,

    /// <summary>Member-to-member email sent through Humans. Default on.</summary>
    FacilitatedMessages = 6,

    /// <summary>Purchase confirmations and event info. Default on; locked on for a member with a matched ticket order.</summary>
    Ticketing = 7,

    /// <summary>Shift changes and schedule updates. Default on.</summary>
    VolunteerUpdates = 8,

    /// <summary>Drive permissions and team member adds/removes. Default on.</summary>
    TeamUpdates = 9,
}

public static class MessageCategoryExtensions
{
    public static bool IsAlwaysOn(this MessageCategory category) => category is
        MessageCategory.System or MessageCategory.CampaignCodes;

    /// <summary>
    /// The value a category reads as when the member has no row. Marketing is opt-in (GDPR); everything
    /// else defaults on. Every reader of a missing row goes through this.
    /// </summary>
    public static bool DefaultOptedOut(this MessageCategory category) =>
        category is MessageCategory.Marketing;

    /// <summary>In display order.</summary>
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
