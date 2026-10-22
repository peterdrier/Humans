using Humans.Feedback.Contracts;
using NodaTime;

namespace Humans.Feedback.Domain;

internal sealed class FeedbackReport
{
    public Guid Id { get; init; }

    public Guid UserId { get; init; }

    public FeedbackCategory Category { get; set; }
    public string Description { get; set; } = string.Empty;
    public string PageUrl { get; set; } = string.Empty;
    public string? UserAgent { get; set; }
    public string? AdditionalContext { get; set; }

    public string? ScreenshotFileName { get; set; }
    public string? ScreenshotStoragePath { get; set; }
    public string? ScreenshotContentType { get; set; }

    public FeedbackStatus Status { get; set; } = FeedbackStatus.Open;
    public int? GitHubIssueNumber { get; set; }
    public Instant? LastReporterMessageAt { get; set; }
    public Instant? LastAdminMessageAt { get; set; }

    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }

    /// <summary>
    /// Vestigial for new rows — no creation path remains. Historical rows hold
    /// UserReport or AgentUnresolved (agent-created); do not assume UserReport when reading.
    /// </summary>
    public FeedbackSource Source { get; set; } = FeedbackSource.UserReport;

    public Guid? AgentConversationId { get; set; }

    public Instant? ResolvedAt { get; set; }

    public Guid? ResolvedByUserId { get; set; }

    public Guid? AssignedToUserId { get; set; }

    public Guid? AssignedToTeamId { get; set; }

    public ICollection<FeedbackMessage> Messages { get; set; } = new List<FeedbackMessage>();
}
