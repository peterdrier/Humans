using NodaTime;
using Humans.Domain.Enums;

namespace Humans.Domain.Entities;

public class Issue
{
    public Guid Id { get; init; }

    public Guid ReporterUserId { get; init; }

    /// <summary>
    /// Section the issue is about (drives routing). Null = unknown → Admin queue.
    /// One of the <c>IssueSectionRouting</c> known values; stored as string.
    /// </summary>
    public string? Section { get; set; }

    public IssueCategory Category { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>Captured by the floating widget. Null for /Issues/New and API submissions.</summary>
    public string? PageUrl { get; set; }
    public string? UserAgent { get; set; }
    public string? AdditionalContext { get; set; }

    public string? ScreenshotFileName { get; set; }
    public string? ScreenshotStoragePath { get; set; }
    public string? ScreenshotContentType { get; set; }

    public IssueStatus Status { get; set; } = IssueStatus.Triage;
    public int? GitHubIssueNumber { get; set; }
    public LocalDate? DueDate { get; set; }

    public Guid? AssigneeUserId { get; set; }

    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }
    public Instant? ResolvedAt { get; set; }

    public Guid? ResolvedByUserId { get; set; }

    public ICollection<IssueComment> Comments { get; set; } = new List<IssueComment>();
}
