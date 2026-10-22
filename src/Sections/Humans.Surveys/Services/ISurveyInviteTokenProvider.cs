namespace Humans.Surveys.Services;

/// <summary>The token carries only the invitation id.</summary>
internal interface ISurveyInviteTokenProvider
{
    string Create(Guid invitationId);

    /// <summary>Null if invalid, tampered or expired.</summary>
    Guid? Resolve(string token);
}
