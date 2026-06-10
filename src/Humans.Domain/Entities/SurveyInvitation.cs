using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Domain.Entities;

public class SurveyInvitation
{
    public Guid Id { get; init; }
    public Guid SurveyId { get; init; }
    public Guid UserId { get; init; }   // bare FK: no nav, no cross-section EF FK constraint; resolve via IUserServiceRead
    public Instant? SentAt { get; set; }
    public EmailOutboxStatus? LatestEmailStatus { get; set; }
    public Instant? ReminderSentAt { get; set; }
    public bool Completed { get; set; }   // flag only — NO completion timestamp (a precise time would correlate with an anon/completion-tracked response's SubmittedAt and unmask)
    public bool Started { get; set; }     // funnel "started" — set on first advance past intro; bool only, no timestamp
    public Instant CreatedAt { get; init; }
}
