using NodaTime;

namespace Humans.Feedback.Domain;

internal sealed class FeedbackReport
{
    public Guid Id { get; init; }

    /// <summary>
    /// FK column only — no navigation property. Resolve the reporter's
    /// display name via <c>IUserServiceRead.GetUserInfosAsync</c>.
    /// </summary>
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

    /// <summary>Defaults to UserReport; set to AgentUnresolved when created by the agent's route_to_feedback tool.</summary>
    public FeedbackSource Source { get; set; } = FeedbackSource.UserReport;

    /// <summary>
    /// FK column only — no navigation property and no EF FK constraint to
    /// agent_conversations. Agent is a self-contained section; cross-section
    /// joins are not modeled in EF. Resolve transcripts via the Agent
    /// section's services when needed.
    /// </summary>
    public Guid? AgentConversationId { get; set; }

    public Instant? ResolvedAt { get; set; }

    /// <summary>
    /// FK column only — no navigation property. Resolve via
    /// <c>IUserServiceRead.GetUserInfosAsync</c>.
    /// </summary>
    public Guid? ResolvedByUserId { get; set; }

    /// <summary>
    /// FK column only — no navigation property. Resolve via
    /// <c>IUserServiceRead.GetUserInfosAsync</c>.
    /// </summary>
    public Guid? AssignedToUserId { get; set; }

    /// <summary>
    /// FK column only — no navigation property. Resolve via
    /// <c>ITeamServiceRead</c>.
    /// </summary>
    public Guid? AssignedToTeamId { get; set; }

    public ICollection<FeedbackMessage> Messages { get; set; } = new List<FeedbackMessage>();
}
