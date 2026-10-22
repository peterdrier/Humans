namespace Humans.Users.Contracts;

/// <summary>
/// The frozen <c>communication_preferences.Category</c> type, so the legacy entity never names an Email type; the
/// <c>email.communication-preferences</c> copy maps by name. Stored by name: never rename.
/// </summary>
public enum LegacyMessageCategory
{
    System = 0,
    EventOperations = 1,
    CommunityUpdates = 2,
    Marketing = 3,
    Governance = 4,
    CampaignCodes = 5,
    FacilitatedMessages = 6,
    Ticketing = 7,
    VolunteerUpdates = 8,
    TeamUpdates = 9,
}
