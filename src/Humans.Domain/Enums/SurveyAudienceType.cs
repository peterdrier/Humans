namespace Humans.Domain.Enums;

/// <summary>
/// Target-predicate kinds an admin can resolve into an invitation set. The resolver lives in the
/// service and reads cross-section membership via I…ServiceRead interfaces. Provisional placeholder
/// for the locked idempotent send model (plan Deviation #2).
/// </summary>
public enum SurveyAudienceType
{
    Team = 0,
    AllActiveMembers = 1,
    TicketHolders = 2,
    ShiftParticipants = 3
}
