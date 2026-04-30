using System.ComponentModel.DataAnnotations;
using Humans.Domain.Enums;

namespace Humans.Web.Models;

public class MySubmissionsViewModel
{
    public int SubmittedCount { get; set; }
    public int ApprovedCount { get; set; }
    public int PendingCount { get; set; }

    public bool IsSubmissionOpen { get; set; }
    public DateTime? SubmissionOpenAt { get; set; }
    public DateTime? SubmissionCloseAt { get; set; }
    public string? TimeZoneId { get; set; }

    public List<IndividualEventRowViewModel> Events { get; set; } = [];
}

public class IndividualEventRowViewModel
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string VenueName { get; set; } = string.Empty;
    public string CategoryName { get; set; } = string.Empty;
    public DateTime StartAt { get; set; }
    public int DurationMinutes { get; set; }
    public GuideEventStatus Status { get; set; }
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

public class IndividualEventFormViewModel
{
    public Guid? Id { get; set; }

    [Required]
    [MaxLength(80)]
    public string Title { get; set; } = string.Empty;

    [Required]
    [MaxLength(450)]
    public string Description { get; set; } = string.Empty;

    [Required]
    [Display(Name = "Category")]
    public Guid CategoryId { get; set; }

    [Required]
    [Display(Name = "Venue")]
    public Guid VenueId { get; set; }

    [Required]
    [Display(Name = "Date")]
    public DateTime StartDate { get; set; }

    [Required]
    [Display(Name = "Start Time")]
    public TimeSpan StartTime { get; set; }

    [Required]
    [Range(15, 1440)]
    [Display(Name = "Duration (minutes)")]
    public int DurationMinutes { get; set; } = 60;

    [Display(Name = "All day")]
    public bool IsAllDay { get; set; }

    [MaxLength(120)]
    [Display(Name = "Location Note")]
    public string? LocationNote { get; set; }

    [Display(Name = "Recurring")]
    public bool IsRecurring { get; set; }

    [Display(Name = "Recurrence Days")]
    public string? RecurrenceDays { get; set; }

    // Dropdown data
    public List<CategoryOptionViewModel> Categories { get; set; } = [];
    public List<VenueOptionViewModel> Venues { get; set; } = [];
    public List<EventDayOptionViewModel> EventDays { get; set; } = [];
    public string? TimeZoneId { get; set; }

    public bool IsResubmit { get; set; }
}

public class ScheduleViewModel
{
    public string? TimeZoneId { get; set; }
    public List<ScheduleDayGroup> DayGroups { get; set; } = [];
}

public class ScheduleDayGroup
{
    public string DayLabel { get; set; } = string.Empty;
    public List<ScheduleItemViewModel> Items { get; set; } = [];
}

public class ScheduleItemViewModel
{
    public Guid EventId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string CategoryName { get; set; } = string.Empty;
    public string? CampName { get; set; }
    public string? VenueName { get; set; }
    public string? LocationNote { get; set; }
    public DateTime StartAt { get; set; }
    public int DurationMinutes { get; set; }
    public int DayOffset { get; set; }
    public string DayLabel { get; set; } = string.Empty;
    public NodaTime.Instant StartInstant { get; set; }
    public bool HasConflict { get; set; }
}

public class VenueOptionViewModel
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class BrowseViewModel
{
    public string? TimeZoneId { get; set; }
    public List<BrowseDayGroup> DayGroups { get; set; } = [];
    public List<CategoryOptionViewModel> Categories { get; set; } = [];
    public List<VenueOptionViewModel> Venues { get; set; } = [];
    public List<EventDayOptionViewModel> Days { get; set; } = [];
    public HashSet<Guid> FavouritedEventIds { get; set; } = [];

    // Active filters (round-tripped via query string)
    public HashSet<int> FilterDays { get; set; } = [];
    public Guid? FilterCategoryId { get; set; }
    public Guid? FilterVenueId { get; set; }
    public string? SearchQuery { get; set; }
    public bool FavouritesOnly { get; set; }
}

public class BrowseDayGroup
{
    public int DayOffset { get; set; }
    public string DayLabel { get; set; } = string.Empty;
    public List<BrowseEventItem> Items { get; set; } = [];
}

public class BrowseEventItem
{
    public Guid EventId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string CategoryName { get; set; } = string.Empty;
    public string? CampName { get; set; }
    public string? VenueName { get; set; }
    public string? LocationNote { get; set; }
    public DateTime StartAt { get; set; }
    public int DurationMinutes { get; set; }
    public int DayOffset { get; set; }
    public bool IsFavourited { get; set; }
    public string? SubmitterName { get; set; }
}

public class BrowseCampOption
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}
