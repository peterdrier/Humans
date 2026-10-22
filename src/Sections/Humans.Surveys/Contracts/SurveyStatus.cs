namespace Humans.Surveys.Contracts;

/// <summary>
/// Approval opens the survey and sends invitations in one step; rejection returns it to <see cref="Draft"/> with a
/// note. Responses are accepted only while <see cref="Open"/>.
/// </summary>
public enum SurveyStatus
{
    Draft = 0,
    Open = 1,
    Closed = 2,
    PendingApproval = 3
}
