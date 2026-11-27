namespace Humans.Surveys.Services;

/// <summary>The token carries only the invitation id.</summary>
internal interface ISurveyInviteTokenProvider
{
    string Create(Guid invitationId);

    /// <summary>The invitation id, or the reason the token yields none.</summary>
    (Guid? InvitationId, SurveyLinkFailure? Failure) Resolve(string token);
}
