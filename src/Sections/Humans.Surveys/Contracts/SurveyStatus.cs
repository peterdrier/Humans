namespace Humans.Surveys.Contracts;

/// <summary>
/// Survey lifecycle. Authored in <see cref="Draft"/>; a self-service author submits it for
/// <see cref="PendingApproval"/>; Board/Admin approve (opens and sends invitations in one step) or
/// reject (back to <see cref="Draft"/>, with a note); accepts responses while <see cref="Open"/>;
/// no longer accepts responses once <see cref="Closed"/>.
/// </summary>
public enum SurveyStatus
{
    Draft = 0,
    Open = 1,
    Closed = 2,
    PendingApproval = 3
}
