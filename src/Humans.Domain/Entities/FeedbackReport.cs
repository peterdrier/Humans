using NodaTime;
using Humans.Domain.Enums;

namespace Humans.Domain.Entities;

public class FeedbackReport
{
    public Guid Id { get; init; }

    public Guid UserId { get; init; }

    /// <summary>
    /// Cross-domain navigation to the reporter's <see cref="User"/>. Kept so
    /// that controllers and views can still read <c>report.User.DisplayName</c>
    /// after the service populates the nav in memory from
    /// <c>IUserService.GetByIdsAsync</c>. Repositories must not
    /// <c>.Include()</c> this property (design-rules §6). Callers in new code
    /// should resolve the user via <c>IUserService</c> directly and stop
    /// navigating this property.
    /// </summary>
    [Obsolete("Cross-domain nav — resolve via IUserService instead of navigating FeedbackReport.User. See design-rules §6c.")]
    public User User { get; set; } = null!;

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

    public Guid? AgentConversationId { get; set; }
    public AgentConversation? AgentConversation { get; set; }

    public Instant? ResolvedAt { get; set; }

    public Guid? ResolvedByUserId { get; set; }

    /// <summary>
    /// Cross-domain navigation to the resolver's <see cref="User"/>.
    /// Service stitches this in memory when rendering reports; repositories
    /// must not <c>.Include()</c> it.
    /// </summary>
    [Obsolete("Cross-domain nav — resolve via IUserService instead of navigating FeedbackReport.ResolvedByUser. See design-rules §6c.")]
    public User? ResolvedByUser { get; set; }

    public Guid? AssignedToUserId { get; set; }

    /// <summary>
    /// Cross-domain navigation to the assignee's <see cref="User"/>.
    /// Service stitches this in memory when rendering reports; repositories
    /// must not <c>.Include()</c> it.
    /// </summary>
    [Obsolete("Cross-domain nav — resolve via IUserService instead of navigating FeedbackReport.AssignedToUser. See design-rules §6c.")]
    public User? AssignedToUser { get; set; }

    public Guid? AssignedToTeamId { get; set; }

    /// <summary>
    /// Cross-domain navigation to the assigned <see cref="Team"/>.
    /// Service stitches this in memory when rendering reports; repositories
    /// must not <c>.Include()</c> it.
    /// </summary>
    [Obsolete("Cross-domain nav — resolve via ITeamService.GetTeamNamesByIdsAsync instead of navigating FeedbackReport.AssignedToTeam. See design-rules §6c.")]
    public Team? AssignedToTeam { get; set; }

    public ICollection<FeedbackMessage> Messages { get; set; } = new List<FeedbackMessage>();
}
