namespace Humans.Surveys.Domain;

/// <summary>
/// Target-predicate kinds an admin can resolve into an invitation set. The resolver lives in the
/// service and reads cross-section membership via I…ServiceRead interfaces.
/// </summary>
internal enum SurveyAudienceType
{
    Team = 0,
    AllActiveMembers = 1,
    TicketHolders = 2,
    ShiftParticipants = 3,
    LoggedInSince = 4,
    Asociados = 5
}
