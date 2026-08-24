using NodaTime;

namespace Humans.Surveys.Domain;

internal sealed class SurveyResponse
{
    public Guid Id { get; init; }
    public Guid SurveyId { get; init; }
    public Guid? InvitationId { get; init; }                   // set ONLY for Identified
    public Guid? UserId { get; init; }                         // set ONLY for Identified; bare FK, no nav, no cross-section EF FK constraint
    public ResponseAnonymity Anonymity { get; init; }
    public SurveyInputMethod InputMethod { get; set; }         // UserSpecificLink vs Slug — follows the route used to finalise a draft
    public string Culture { get; set; } = "en";                 // follows the culture used to finalise a draft
    public Instant? SubmittedAt { get; set; }                  // null = in-progress draft (Identified only; resumable §8); set at final submit
    public ICollection<SurveyAnswer> Answers { get; set; } = new List<SurveyAnswer>();
}
