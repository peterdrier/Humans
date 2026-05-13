using System.ComponentModel.DataAnnotations;
using Humans.Domain.Enums;

namespace Humans.Web.Models;

public class CampEventsTabViewModel
{
    public Guid CampId { get; set; }
    public string CampName { get; set; } = string.Empty;
    public string CampSlug { get; set; } = string.Empty;

    public int SubmittedCount { get; set; }
    public int ApprovedCount { get; set; }
    public int PendingCount { get; set; }

    public bool IsSubmissionOpen { get; set; }
    public DateTime? SubmissionOpenAt { get; set; }
    public DateTime? SubmissionCloseAt { get; set; }
    public string? TimeZoneId { get; set; }

    public List<CampEventRowViewModel> Events { get; set; } = [];
}

public class CampEventRowViewModel
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string CategoryName { get; set; } = string.Empty;
    public DateTime StartAt { get; set; }
    public int DurationMinutes { get; set; }
    public GuideEventStatus Status { get; set; }
    public int PriorityRank { get; set; }
    public bool CanEdit { get; set; }
    public bool CanWithdraw { get; set; }

    public string StatusBadgeClass => Status switch
    {
        GuideEventStatus.Draft => "bg-secondary",
        GuideEventStatus.Pending => "bg-warning text-dark",
        GuideEventStatus.Approved => "bg-success",
        GuideEventStatus.Rejected => "bg-danger",
        GuideEventStatus.ResubmitRequested => "bg-info",
        GuideEventStatus.Withdrawn => "bg-dark",
        _ => "bg-secondary"
    };
}

public class CampEventFormViewModel
{
    public Guid? Id { get; set; }
    public Guid CampId { get; set; }
    public string CampName { get; set; } = string.Empty;
    public string CampSlug { get; set; } = string.Empty;

    [Required]
    [MaxLength(80)]
    public string Title { get; set; } = string.Empty;

    [Required]
    [MaxLength(300)]
    public string Description { get; set; } = string.Empty;

    [Required]
    [Display(Name = "Category")]
    public Guid CategoryId { get; set; }

    [Required]
    [Display(Name = "Date")]
    public DateTime StartDate { get; set; }

    [Required]
    [Display(Name = "Start Time")]
    public TimeSpan StartTime { get; set; }

    [Required]
    [Range(15, 480)]
    [Display(Name = "Duration (minutes)")]
    public int DurationMinutes { get; set; } = 60;

    [MaxLength(120)]
    [Display(Name = "Location Note")]
    public string? LocationNote { get; set; }

    [Display(Name = "Recurring")]
    public bool IsRecurring { get; set; }

    [Display(Name = "Recurrence Days")]
    public string? RecurrenceDays { get; set; }

    [Required]
    [Range(1, 100)]
    [Display(Name = "Priority Rank")]
    public int PriorityRank { get; set; } = 1;

    // Dropdown data
    public List<CategoryOptionViewModel> Categories { get; set; } = [];
    public List<EventDayOptionViewModel> EventDays { get; set; } = [];
    public string? TimeZoneId { get; set; }

    /// <summary>
    /// Whether this is a resubmission of a rejected/resubmit-requested event.
    /// </summary>
    public bool IsResubmit { get; set; }
}

public class CategoryOptionViewModel
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class EventDayOptionViewModel
{
    public int DayOffset { get; set; }
    public string Label { get; set; } = string.Empty;
    public DateTime Date { get; set; }
}
