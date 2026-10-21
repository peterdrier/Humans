namespace Humans.Users.Contracts;

/// <summary>
/// The type of the frozen <c>communication_preferences.Category</c> column. The live category is Email's
/// <c>MessageCategory</c> in <c>email_communication_preferences</c> (Users decomposition commit 14).
/// The legacy table keeps this copy so its entity and the obsolete <see cref="UserInfo.CommunicationPreferences"/>
/// never name an Email type, and the <c>email.communication-preferences</c> copy maps it by name.
/// Deleted with that migration's retirement PR. Member names are stored as strings: never rename.
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
