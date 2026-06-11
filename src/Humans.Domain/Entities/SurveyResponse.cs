using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Domain.Entities;

public class SurveyResponse
{
    public Guid Id { get; init; }
    public Guid SurveyId { get; init; }
    public Guid? InvitationId { get; init; }                   // set ONLY for Identified
    public Guid? UserId { get; init; }                         // set ONLY for Identified; bare FK, no nav, no cross-section EF FK constraint
    public ResponseAnonymity Anonymity { get; init; }
    public SurveyInputMethod InputMethod { get; init; }        // UserSpecificLink vs Slug — funnel split by entry path
    public string Culture { get; init; } = "en";
    public Instant? SubmittedAt { get; set; }                  // null = in-progress draft (Identified only; resumable §8); set at final submit
    public ICollection<SurveyAnswer> Answers { get; set; } = new List<SurveyAnswer>();
}
