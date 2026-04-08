using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Humans.Application.Interfaces;
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

public class FeedbackPageViewModel
{
    public List<FeedbackListItemViewModel> Reports { get; set; } = new();
    public FeedbackStatus? StatusFilter { get; set; }
    public FeedbackCategory? CategoryFilter { get; set; }
    public Guid? ReporterFilter { get; set; }
    public List<ReporterDropdownItem> Reporters { get; set; } = new();
    public Guid? AssignedToFilter { get; set; }
    public Guid? TeamFilter { get; set; }
    public bool UnassignedFilter { get; set; }
    public bool IsAdmin { get; set; }
    public Guid? SelectedReportId { get; set; }
    public Guid CurrentUserId { get; set; }
    public List<AssigneeOption> AssigneeOptions { get; set; } = new();
    public List<TeamOptionDto> TeamOptions { get; set; } = new();
}

public class AssigneeOption
{
    public Guid Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
}

public class ReporterDropdownItem
{
    public Guid UserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public int Count { get; set; }
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
    public int MessageCount { get; set; }
    public bool NeedsReply { get; set; }
    public int? GitHubIssueNumber { get; set; }
    public string? AssignedToName { get; set; }
    public Guid? AssignedToUserId { get; set; }
    public string? AssignedToTeamName { get; set; }
    public Guid? AssignedToTeamId { get; set; }
}

public class FeedbackDetailViewModel
{
    public Guid Id { get; set; }
    public FeedbackCategory Category { get; set; }
    public FeedbackStatus Status { get; set; }
    public string Description { get; set; } = string.Empty;
    public string PageUrl { get; set; } = string.Empty;
    public string? UserAgent { get; set; }
    public string? AdditionalContext { get; set; }
    public string? ScreenshotUrl { get; set; }
    public string ReporterName { get; set; } = string.Empty;
    public Guid ReporterUserId { get; set; }
    public int? GitHubIssueNumber { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public string? ResolvedByName { get; set; }
    public bool IsAdmin { get; set; }
    public Guid? AssignedToUserId { get; set; }
    public string? AssignedToName { get; set; }
    public Guid? AssignedToTeamId { get; set; }
    public string? AssignedToTeamName { get; set; }
    public List<AssigneeOption> AssigneeOptions { get; set; } = new();
    public List<TeamOptionDto> TeamOptions { get; set; } = new();
    public List<FeedbackMessageViewModel> Messages { get; set; } = new();
}

public class FeedbackMessageViewModel
{
    public Guid Id { get; set; }
    public string SenderName { get; set; } = string.Empty;
    public Guid? SenderUserId { get; set; }
    public string Content { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public bool IsReporter { get; set; }
}

public class UpdateFeedbackStatusModel
{
    [Required]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public FeedbackStatus Status { get; set; }
}

public class SetGitHubIssueModel
{
    public int? IssueNumber { get; set; }
}

public class PostFeedbackMessageModel
{
    [Required]
    [StringLength(5000)]
    public string Content { get; set; } = string.Empty;
}

public class UpdateFeedbackAssignmentModel
{
    public Guid? AssignedToUserId { get; set; }
    public Guid? AssignedToTeamId { get; set; }
}
