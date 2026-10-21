namespace Humans.Users.Domain;

/// <summary>
/// The type of the frozen <c>communication_preferences.Category</c> column, held only until its drop
/// (USERS-20). The live category is Email's <c>MessageCategory</c> in
/// <c>email_communication_preferences</c> (Users decomposition commit 14). Member names are stored as
/// strings: never rename.
/// </summary>
internal enum LegacyMessageCategory
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
