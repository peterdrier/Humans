namespace Humans.Application.Interfaces.Surveys;

/// <summary>
/// Mints and resolves tokenised survey-invitation links via ASP.NET Data Protection. The token
/// carries only the invitation id (time-limited). Implementation lives in Infrastructure.
/// </summary>
public interface ISurveyInviteTokenProvider
{
    /// <summary>Creates a time-limited token encoding the invitation id.</summary>
    string Create(Guid invitationId);

    /// <summary>Resolves a token back to its invitation id, or null if invalid/tampered/expired.</summary>
    Guid? Resolve(string token);
}
