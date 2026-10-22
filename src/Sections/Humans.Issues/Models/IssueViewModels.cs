using Microsoft.Extensions.Localization;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using NodaTime;
using Humans.Issues.Contracts;
using Humans.Issues.Domain;
using Humans.Base.Models;

namespace Humans.Issues.Models;

/// <summary>Bound by both the <c>/Issues/New</c> form and the floating widget's AJAX post.</summary>
internal sealed class SubmitIssueViewModel
{
    [Required(ErrorMessage = "Validation_Required")]
    [StringLength(200, ErrorMessage = "Validation_MaxLength")]
    public string Title { get; set; } = string.Empty;

    [Required(ErrorMessage = "Validation_Required")]
    [StringLength(5000, ErrorMessage = "Validation_MaxLength")]
    public string Description { get; set; } = string.Empty;

    [Required(ErrorMessage = "Validation_Required")]
    [BindRequired]
    public IssueCategory Category { get; set; }

    /// <summary>
    /// Null on post means the controller infers it from <see cref="PageUrl"/>. An unrouted value degrades to the
    /// Admin queue; the length cap is the input-layer half of the service's guard.
    /// </summary>
    [StringLength(64, ErrorMessage = "Validation_MaxLength")]
    public string? Section { get; set; }

    [StringLength(2000, ErrorMessage = "Validation_MaxLength")]
    public string? PageUrl { get; set; }

    [StringLength(1000, ErrorMessage = "Validation_MaxLength")]
    public string? UserAgent { get; set; }

    [StringLength(2000, ErrorMessage = "Validation_MaxLength")]
    public string? AdditionalContext { get; set; }

    public IFormFile? Screenshot { get; set; }

    public LocalDate? DueDate { get; set; }
}

/// <summary>"Open" means not closed, not the Open status.</summary>
internal enum IssueViewMode
{
    All,
    Open,    // Non-terminal: Triage, Open, InProgress — wider than the nav badge
    Mine,
    Closed
}

internal sealed class IssuePageViewModel
{
    public List<IssueListItemViewModel> Issues { get; set; } = [];

    public IssueViewMode View { get; set; } = IssueViewMode.All;
    public IssueCategory? CategoryFilter { get; set; }
    public string? SectionFilter { get; set; }
    public Guid? ReporterFilter { get; set; }
    public string? SearchText { get; set; }

    public bool IsAdmin { get; set; }

    public Guid? SelectedIssueId { get; set; }

    /// <summary>Admin sees all; others see the sections they own.</summary>
    public List<SectionOption> SectionOptions { get; set; } = [];

    /// <summary>Admin only: non-admins only see their own queue.</summary>
    public List<ReporterDropdownItem> Reporters { get; set; } = [];

    public IssueCategory[] CategoryValues => Enum.GetValues<IssueCategory>();

    public int OpenCount { get; set; }
    public int TotalCount { get; set; }
}

internal sealed class SectionOption
{
    public string Section { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
}

internal sealed class IssueListItemViewModel
{
    public Guid Id { get; set; }
    public IssueStatus Status { get; set; }
    public IssueCategory Category { get; set; }
    public string AreaLabel { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public Guid ReporterUserId { get; set; }
    public DateTime LastUpdate { get; set; }
    public int CommentCount { get; set; }
    public Guid? AssigneeUserId { get; set; }
    public int? GitHubIssueNumber { get; set; }
}

internal sealed class IssueDetailViewModel
{
    public Guid Id { get; set; }
    public IssueStatus Status { get; set; }
    public IssueCategory Category { get; set; }
    public string? Section { get; set; }
    public string AreaLabel { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? PageUrl { get; set; }
    public string? UserAgent { get; set; }
    public string? AdditionalContext { get; set; }
    public string? ScreenshotUrl { get; set; }

    public Guid ReporterUserId { get; set; }
    public Guid? AssigneeUserId { get; set; }
    public int? GitHubIssueNumber { get; set; }
    public LocalDate? DueDate { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public string? ResolvedByName { get; set; }

    public bool IsHandler { get; set; }
    public bool IsReporter { get; set; }

    public List<IssueThreadEventViewModel> Thread { get; set; } = [];

    /// <summary>Empty for non-handlers.</summary>
    public List<AssigneeOption> AssigneeOptions { get; set; } = [];
}

internal sealed class IssueThreadEventViewModel
{
    /// <summary>"comment" or "audit".</summary>
    public string Type { get; set; } = string.Empty;
    public DateTime At { get; set; }
    public Guid? ActorUserId { get; set; }
    public string? ActorName { get; set; }

    public string? Content { get; set; }

    public string? Description { get; set; }

    /// <summary>Comment rows only.</summary>
    public bool ActorIsReporter { get; set; }
}

internal sealed class PostIssueCommentModel
{
    [Required]
    [StringLength(5000)]
    public string Content { get; set; } = string.Empty;

    /// <summary>Also moves the issue to <see cref="IssueStatus.Resolved"/> after posting.</summary>
    public bool ResolveOnPost { get; set; }
}

internal sealed class UpdateIssueStatusModel
{
    [Required]
    [BindRequired]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public IssueStatus Status { get; set; }
}

internal sealed class UpdateIssueAssigneeModel
{
    public Guid? AssigneeUserId { get; set; }
}

internal sealed class UpdateIssueSectionModel
{
    /// <summary>Capped at the column width; the service holds the invariant.</summary>
    [StringLength(64)]
    public string? Section { get; set; }
}

internal sealed class SetIssueGitHubIssueModel
{
    public int? GitHubIssueNumber { get; set; }
}

internal static class AreaLabelMap
{
    /// <summary>Localizes known labels and the null/General fallback; unknown routing keys keep their spelling.</summary>
    public static string LabelFor(string? section, IStringLocalizer<IssuesResource> localizer, IssueSectionRouting routing)
    {
        if (section is null) return localizer["Issues_Area_General"].Value;
        return routing.AreaLabelResourceKeys.TryGetValue(section, out var key) ? localizer[key].Value : section;
    }
}
