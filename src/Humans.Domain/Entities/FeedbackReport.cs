using NodaTime;
using Humans.Domain.Enums;

namespace Humans.Domain.Entities;

public class FeedbackReport
{
    public Guid Id { get; init; }

    public Guid UserId { get; init; }
    public User User { get; set; } = null!;

    public FeedbackCategory Category { get; set; }
    public string Description { get; set; } = string.Empty;
    public string PageUrl { get; set; } = string.Empty;
    public string? UserAgent { get; set; }

    public string? ScreenshotFileName { get; set; }
    public string? ScreenshotStoragePath { get; set; }
    public string? ScreenshotContentType { get; set; }

    public FeedbackStatus Status { get; set; } = FeedbackStatus.Open;
    public string? AdminNotes { get; set; }
    public int? GitHubIssueNumber { get; set; }
    public Instant? AdminResponseSentAt { get; set; }

    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }
    public Instant? ResolvedAt { get; set; }

    public Guid? ResolvedByUserId { get; set; }
    public User? ResolvedByUser { get; set; }
}
