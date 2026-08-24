using Humans.Surveys.Contracts;
using NodaTime;

namespace Humans.Surveys.Domain;

internal sealed class Survey
{
    public Guid Id { get; init; }
    public LocalizedText Title { get; set; } = LocalizedText.Empty;
    public LocalizedText Intro { get; set; } = LocalizedText.Empty;
    public LocalizedText ThankYou { get; set; } = LocalizedText.Empty;
    public LocalizedText InvitationEmailSubject { get; set; } = LocalizedText.Empty;
    public LocalizedText InvitationEmailMessage { get; set; } = LocalizedText.Empty;
    public string DefaultCulture { get; set; } = "en";
    public bool AllowAnonymous { get; set; }
    public SurveyStatus Status { get; set; } = SurveyStatus.Draft;
    public Instant? OpensAt { get; set; }
    public Instant? ClosesAt { get; set; }
    public SurveyAudienceType? AudienceType { get; set; }
    public Guid? AudienceTeamId { get; set; }                 // bare Guid when AudienceType == Team; no nav, no cross-section FK constraint
    public Instant? AudienceLoggedInSince { get; set; }       // cutoff when AudienceType == LoggedInSince; users with LastLoginAt >= cutoff match
    public string? PublicSlug { get; set; }                   // public answering link; requires AllowAnonymous; null = invite-only
    public int PublicStartedCount { get; set; }               // all slug-path starts; tracked users also have a per-person ledger row
    public Guid CreatedByUserId { get; init; }                // bare FK: no nav, no cross-section EF FK constraint; resolve via IUserServiceRead
    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }
    public ICollection<SurveyQuestion> Questions { get; set; } = new List<SurveyQuestion>();
}
