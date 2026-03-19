using System.ComponentModel.DataAnnotations;
using Humans.Domain.Enums;

namespace Humans.Web.Models;

public class SubmitFeedbackViewModel
{
    [Required]
    public FeedbackCategory Category { get; set; }

    [Required]
    [StringLength(5000)]
    public string Description { get; set; } = string.Empty;

    [StringLength(2000)]
    public string PageUrl { get; set; } = string.Empty;

    [StringLength(1000)]
    public string? UserAgent { get; set; }

    public IFormFile? Screenshot { get; set; }
}

public class FeedbackListViewModel
{
    public List<FeedbackListItemViewModel> Reports { get; set; } = [];
    public FeedbackStatus? StatusFilter { get; set; }
    public FeedbackCategory? CategoryFilter { get; set; }
}

public class FeedbackListItemViewModel
{
    public Guid Id { get; set; }
    public FeedbackCategory Category { get; set; }
    public FeedbackStatus Status { get; set; }
    public string Description { get; set; } = string.Empty;
    public string ReporterName { get; set; } = string.Empty;
    public Guid ReporterUserId { get; set; }
    public string PageUrl { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public bool HasScreenshot { get; set; }
}

public class FeedbackDetailViewModel
{
    public Guid Id { get; set; }
    public FeedbackCategory Category { get; set; }
    public FeedbackStatus Status { get; set; }
    public string Description { get; set; } = string.Empty;
    public string PageUrl { get; set; } = string.Empty;
    public string? UserAgent { get; set; }
    public string? ScreenshotUrl { get; set; }
    public string ReporterName { get; set; } = string.Empty;
    public Guid ReporterUserId { get; set; }
    public string? AdminNotes { get; set; }
    public int? GitHubIssueNumber { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? AdminResponseSentAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public string? ResolvedByName { get; set; }
}

public class UpdateFeedbackStatusModel
{
    [Required]
    public FeedbackStatus Status { get; set; }
}

public class UpdateFeedbackNotesModel
{
    [StringLength(5000)]
    public string? Notes { get; set; }
}

public class SetGitHubIssueModel
{
    public int? IssueNumber { get; set; }
}

public class SendFeedbackResponseModel
{
    [Required]
    [StringLength(5000)]
    public string Message { get; set; } = string.Empty;
}
