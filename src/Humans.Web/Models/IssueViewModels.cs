using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using NodaTime;
using Humans.Application.Interfaces.Issues;
using Humans.Domain.Constants;
using Humans.Domain.Enums;

namespace Humans.Web.Models;

/// <summary>
/// Bound by the <c>/Issues/New</c> form post AND the floating widget AJAX path.
/// Title + Description + Category are required; everything else is optional.
/// </summary>
public class SubmitIssueViewModel
{
    [Required]
    [StringLength(200)]
    public string Title { get; set; } = string.Empty;

    [Required]
    [StringLength(5000)]
    public string Description { get; set; } = string.Empty;

    [Required]
    public IssueCategory Category { get; set; }

    /// <summary>
    /// Technical section name (matches <see cref="IssueSectionRouting"/> values).
    /// If null when the form is posted, the controller infers it from
    /// <see cref="PageUrl"/> via <c>IssueSectionInference.FromPath</c>.
    /// </summary>
    public string? Section { get; set; }

    [StringLength(2000)]
    public string? PageUrl { get; set; }

    [StringLength(1000)]
    public string? UserAgent { get; set; }

    [StringLength(2000)]
    public string? AdditionalContext { get; set; }

    public IFormFile? Screenshot { get; set; }

    public LocalDate? DueDate { get; set; }
}

/// <summary>
/// Quick-filter "view" pill for the Issues index page. Mutually exclusive
/// with itself (only one is active at a time). Keeps the URL clean and lets
/// "Open" mean "everything not closed" instead of the literal Open enum value.
/// </summary>
public enum IssueViewMode
{
    All,
    Open,    // Statuses ∈ {Triage, Open, InProgress} — matches the nav-badge "actionable" set
    Mine,    // ReporterUserId = current user
    Closed   // Statuses ∈ {Resolved, WontFix, Duplicate}
}

public class IssuePageViewModel
{
    public List<IssueListItemViewModel> Issues { get; set; } = new();

    public IssueViewMode View { get; set; } = IssueViewMode.All;
    public IssueCategory? CategoryFilter { get; set; }
    public string? SectionFilter { get; set; }
    public Guid? ReporterFilter { get; set; }
    public string? SearchText { get; set; }

    public Guid CurrentUserId { get; set; }
    public bool IsAdmin { get; set; }

    public Guid? SelectedIssueId { get; set; }

    /// <summary>Sections this viewer is allowed to filter on (Admin sees all, others see their owned sections).</summary>
    public List<SectionOption> SectionOptions { get; set; } = new();

    /// <summary>Reporter dropdown (Admin only — non-admins only see their own queue).</summary>
    public List<ReporterDropdownItem> Reporters { get; set; } = new();

    /// <summary>All status enum values, exposed so the view doesn't reach into Domain.</summary>
    public IssueStatus[] StatusValues => Enum.GetValues<IssueStatus>();

    /// <summary>All category enum values.</summary>
    public IssueCategory[] CategoryValues => Enum.GetValues<IssueCategory>();

    public int OpenCount { get; set; }
    public int TotalCount { get; set; }
}

public class SectionOption
{
    public string Section { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
}

public class IssueListItemViewModel
{
    public Guid Id { get; set; }
    public IssueStatus Status { get; set; }
    public IssueCategory Category { get; set; }
    public string? Section { get; set; }
    public string AreaLabel { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string ReporterName { get; set; } = string.Empty;
    public Guid ReporterUserId { get; set; }
    public DateTime LastUpdate { get; set; }
    public int CommentCount { get; set; }
    public Guid? AssigneeUserId { get; set; }
    public string? AssigneeName { get; set; }
    public int? GitHubIssueNumber { get; set; }
}

public class IssueDetailViewModel
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

    public string ReporterName { get; set; } = string.Empty;
    public Guid ReporterUserId { get; set; }
    public string? AssigneeName { get; set; }
    public Guid? AssigneeUserId { get; set; }
    public int? GitHubIssueNumber { get; set; }
    public LocalDate? DueDate { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public string? ResolvedByName { get; set; }

    public bool IsHandler { get; set; }
    public bool IsReporter { get; set; }

    public List<IssueThreadEventViewModel> Thread { get; set; } = new();

    /// <summary>
    /// Active humans the handler can assign this issue to. Empty for non-handlers.
    /// Reuses <see cref="AssigneeOption"/> from the Feedback view models so the
    /// shape stays consistent across triage UIs.
    /// </summary>
    public List<AssigneeOption> AssigneeOptions { get; set; } = new();
}

public class IssueThreadEventViewModel
{
    /// <summary>"comment" or "audit".</summary>
    public string Type { get; set; } = string.Empty;
    public DateTime At { get; set; }
    public Guid? ActorUserId { get; set; }
    public string? ActorName { get; set; }

    /// <summary>For "comment" rows.</summary>
    public string? Content { get; set; }

    /// <summary>For "audit" rows.</summary>
    public string? Description { get; set; }

    /// <summary>For "comment" rows — true when the comment author is the issue reporter.</summary>
    public bool ActorIsReporter { get; set; }
}

public class PostIssueCommentModel
{
    [Required]
    [StringLength(5000)]
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// Optional: when true, also moves the issue to <see cref="IssueStatus.Resolved"/>
    /// after posting the comment. Wired up by the "Comment & mark resolved" button.
    /// </summary>
    public bool ResolveOnPost { get; set; }
}

public class UpdateIssueStatusModel
{
    [Required]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public IssueStatus Status { get; set; }
}

public class UpdateIssueAssigneeModel
{
    public Guid? AssigneeUserId { get; set; }
}

public class UpdateIssueSectionModel
{
    public string? Section { get; set; }
}

public class SetIssueGitHubIssueModel
{
    public int? GitHubIssueNumber { get; set; }
}

/// <summary>
/// Friendly labels for each <see cref="IssueSectionRouting"/> value, used by the
/// New-issue form's "Area" select and by the Index/Detail "area" chip.
/// </summary>
public static class AreaLabelMap
{
    // Order matters: New.cshtml and _Detail.cshtml iterate Map directly to
    // populate the Area dropdown. Keep entries sorted alphabetically by label.
    public static readonly IReadOnlyDictionary<string, string> Map =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [IssueSectionRouting.Camps] = "Barrios",
            [IssueSectionRouting.Budget] = "Budget",
            [IssueSectionRouting.CityPlanning] = "City planning",
            [IssueSectionRouting.Legal] = "Legal & consent",
            [IssueSectionRouting.Onboarding] = "Onboarding",
            [IssueSectionRouting.Profiles] = "Profile & onboarding",
            [IssueSectionRouting.Shifts] = "Shifts & volunteering",
            [IssueSectionRouting.Teams] = "Teams",
            [IssueSectionRouting.Tickets] = "Tickets",
            [IssueSectionRouting.Governance] = "Voting & governance",
        };

    /// <summary>Returns the friendly label for a section, or "General" when unmapped/null.</summary>
    public static string LabelFor(string? section)
    {
        if (section is null) return "General";
        return Map.TryGetValue(section, out var label) ? label : section;
    }
}
