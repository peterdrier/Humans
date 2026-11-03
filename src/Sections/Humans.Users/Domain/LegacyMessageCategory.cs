namespace Humans.Users.Domain;

/// <summary>Frozen storage values; names and numeric values stay stable until the table is dropped.</summary>
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
