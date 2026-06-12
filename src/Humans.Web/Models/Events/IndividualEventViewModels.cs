using System.ComponentModel.DataAnnotations;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Events;
using Humans.Domain.Enums;
using Humans.Web.Helpers;
using NodaTime;

namespace Humans.Web.Models.Events;

public class MySubmissionsViewModel
{
    public bool IsSubmissionOpen { get; set; }
    public DateTime? SubmissionOpenAt { get; set; }
    public DateTime? SubmissionCloseAt { get; set; }
    public string? TimeZoneId { get; set; }

    public PersonalSubmissionsBlock Personal { get; set; } = new();
    public List<BarrioSubmissionsBlock> Barrios { get; set; } = [];
}

public class PersonalSubmissionsBlock
{
    public int SubmittedCount { get; set; }
    public int ApprovedCount { get; set; }
    public int PendingCount { get; set; }
    public List<IndividualEventRowViewModel> Events { get; set; } = [];

    public static PersonalSubmissionsBlock From(IReadOnlyList<EventInfo> events, DateTimeZone? tz) => new()
    {
        SubmittedCount = events.Count,
        ApprovedCount = events.Count(e => e.Status == EventStatus.Approved),
        PendingCount = events.Count(e => e.Status == EventStatus.Pending),
        Events = events.OrderByDescending(e => e.SubmittedAt)
            .Select(e => IndividualEventRowViewModel.From(e, tz)).ToList()
    };
}

public class BarrioSubmissionsBlock
{
    public Guid CampId { get; set; }
    public string CampName { get; set; } = string.Empty;
    public string CampSlug { get; set; } = string.Empty;
    public int SubmittedCount { get; set; }
    public int ApprovedCount { get; set; }
    public int PendingCount { get; set; }
    public List<CampEventRowViewModel> Events { get; set; } = [];
    public List<BulkRowError> BulkUploadErrors { get; set; } = [];

    public static BarrioSubmissionsBlock From(CampInfo camp, IReadOnlyList<EventInfo> events, DateTimeZone? tz) => new()
    {
        CampId = camp.Id,
        CampName = camp.Active?.Name ?? camp.Slug,
        CampSlug = camp.Slug,
        SubmittedCount = events.Count,
        ApprovedCount = events.Count(e => e.Status == EventStatus.Approved),
        PendingCount = events.Count(e => e.Status == EventStatus.Pending),
        Events = events.OrderByDescending(e => e.SubmittedAt)
            .Select(e => CampEventRowViewModel.From(e, tz)).ToList()
    };
}

public class BulkRowError
{
    public int RowNumber { get; set; }
    public string Title { get; set; } = string.Empty;
    public List<string> Errors { get; set; } = [];
}

public class IndividualEventRowViewModel
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string VenueName { get; set; } = string.Empty;
    public string CategoryName { get; set; } = string.Empty;
    public DateTime StartAt { get; set; }
    public int DurationMinutes { get; set; }
    public EventStatus Status { get; set; }
    public bool CanEdit { get; set; }
    public bool CanWithdraw { get; set; }

    public string StatusBadgeClass => Status switch
    {
        EventStatus.Draft => "bg-secondary",
        EventStatus.Pending => "bg-warning text-dark",
        EventStatus.Approved => "bg-success",
        EventStatus.Rejected => "bg-danger",
        EventStatus.ResubmitRequested => "bg-info",
        EventStatus.Withdrawn => "bg-dark",
        _ => "bg-secondary"
    };

    public static IndividualEventRowViewModel From(EventInfo e, DateTimeZone? tz) => new()
    {
        Id = e.Id,
        Title = e.Title,
        VenueName = e.VenueName ?? "—",
        CategoryName = e.CategoryName,
        StartAt = EventsTimeHelpers.ToLocalDateTime(e.StartAt, tz),
        DurationMinutes = e.DurationMinutes,
        Status = e.Status,
        CanEdit = e.Status is EventStatus.Draft or EventStatus.Pending or EventStatus.Rejected or EventStatus.ResubmitRequested,
        CanWithdraw = e.Status is EventStatus.Draft or EventStatus.Pending or EventStatus.Approved
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

    [MaxLength(40)]
    [Display(Name = "Host")]
    public string? Host { get; set; }

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

    /// <summary>The backing favourite row's day offset; null = whole-event favourite.</summary>
    public int? FavouriteDayOffset { get; set; }
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

    /// <summary>Day offset the heart toggles: this card's day for recurring events, null (whole event) otherwise.</summary>
    public int? FavouriteDayOffset { get; set; }
    public bool IsFavourited { get; set; }
    public string? SubmitterName { get; set; }
    public string? DisplayHost { get; set; }
}

public class BrowseCampOption
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}
