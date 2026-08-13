using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Humans.Teams.Contracts;
using Humans.Feedback.Domain;
using Humans.UI.Models;

namespace Humans.Feedback.Models;

internal sealed class FeedbackPageViewModel
{
    public List<FeedbackListItemViewModel> Reports { get; set; } = [];
    public FeedbackStatus? StatusFilter { get; set; }
    public FeedbackCategory? CategoryFilter { get; set; }
    public Guid? ReporterFilter { get; set; }
    public List<ReporterDropdownItem> Reporters { get; set; } = [];
    public Guid? AssignedToFilter { get; set; }
    public Guid? TeamFilter { get; set; }
    public bool UnassignedFilter { get; set; }
    public Guid? SelectedReportId { get; set; }
    public Guid CurrentUserId { get; set; }
    public List<AssigneeOption> AssigneeOptions { get; set; } = [];
    public IReadOnlyList<TeamInfo> TeamOptions { get; set; } = [];
}

internal sealed class FeedbackListItemViewModel
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

internal sealed class FeedbackDetailViewModel
{
    public Guid Id { get; set; }
    public FeedbackCategory Category { get; set; }
    public FeedbackStatus Status { get; set; }
    public string Description { get; set; } = string.Empty;
    public string PageUrl { get; set; } = string.Empty;
    public string? UserAgent { get; set; }
    public string? AdditionalContext { get; set; }
    public string? ScreenshotUrl { get; set; }
    public Guid ReporterUserId { get; set; }
    public int? GitHubIssueNumber { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public string? ResolvedByName { get; set; }
    public Guid? AssignedToUserId { get; set; }
    public string? AssignedToName { get; set; }
    public Guid? AssignedToTeamId { get; set; }
    public string? AssignedToTeamName { get; set; }
    public List<AssigneeOption> AssigneeOptions { get; set; } = [];
    public IReadOnlyList<TeamInfo> TeamOptions { get; set; } = [];
    public List<FeedbackMessageViewModel> Messages { get; set; } = [];
}

internal sealed class FeedbackMessageViewModel
{
    public Guid Id { get; set; }
    public string SenderName { get; set; } = string.Empty;
    public Guid? SenderUserId { get; set; }
    public string Content { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public bool IsReporter { get; set; }
}

internal sealed class UpdateFeedbackStatusModel
{
    [Required]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public FeedbackStatus Status { get; set; }
}

internal sealed class SetGitHubIssueModel
{
    public int? IssueNumber { get; set; }
}

internal sealed class PostFeedbackMessageModel
{
    [Required]
    [StringLength(5000)]
    public string Content { get; set; } = string.Empty;
}

internal sealed class UpdateFeedbackAssignmentModel
{
    public Guid? AssignedToUserId { get; set; }
    public Guid? AssignedToTeamId { get; set; }
}
