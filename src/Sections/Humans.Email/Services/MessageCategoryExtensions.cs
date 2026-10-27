using Humans.Email.Contracts;

namespace Humans.Email.Services;

internal static class MessageCategoryExtensions
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
