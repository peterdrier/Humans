using System.ComponentModel.DataAnnotations;
using Humans.Domain.Enums;

namespace Humans.Web.Models.Events;

public class ModerationQueueViewModel
{
    public EventStatus ActiveTab { get; set; } = EventStatus.Pending;
    public int PendingCount { get; set; }
    public int ApprovedCount { get; set; }
    public int RejectedCount { get; set; }
    public int ResubmitRequestedCount { get; set; }
    public int WithdrawnCount { get; set; }
    public string? TimeZoneId { get; set; }
    public List<ModerationEventRowViewModel> Events { get; set; } = [];
}

public class ModerationEventRowViewModel
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string SubmitterName { get; set; } = string.Empty;
    public Guid SubmitterUserId { get; set; }
    public string? CampName { get; set; }
    public string? CampSlug { get; set; }
    public string? VenueName { get; set; }
    public string CategoryName { get; set; } = string.Empty;
    public DateTime StartAt { get; set; }
    public int DurationMinutes { get; set; }
    public string? LocationNote { get; set; }
    public bool IsRecurring { get; set; }
    public string? RecurrenceDays { get; set; }
    public int PriorityRank { get; set; }
    public DateTime SubmittedAt { get; set; }
    public EventStatus Status { get; set; }
    public List<ModerationHistoryItemViewModel> History { get; set; } = [];
    public List<DuplicateCandidateViewModel> DuplicateCandidates { get; set; } = [];

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
}

public class ModerationHistoryItemViewModel
{
    public string ActorName { get; set; } = string.Empty;
    public EventModerationActionType Action { get; set; }
    public string? Reason { get; set; }
    public DateTime CreatedAt { get; set; }

    public string ActionBadgeClass => Action switch
    {
        EventModerationActionType.Approved => "bg-success",
        EventModerationActionType.Rejected => "bg-danger",
        EventModerationActionType.ResubmitRequested => "bg-info",
        EventModerationActionType.Edited => "bg-primary",
        _ => "bg-secondary"
    };
}

public class DuplicateCandidateViewModel
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public DateTime StartAt { get; set; }
    public int DurationMinutes { get; set; }
    public EventStatus Status { get; set; }
}

public class ModerationActionFormModel
{
    public Guid EventId { get; set; }

    [MaxLength(500)]
    public string? Reason { get; set; }
}

/// <summary>
/// Admin / moderator in-place edit form for any event in any state. One form
/// serves both individual events (venue) and camp events (priority rank);
/// <see cref="IsCampEvent"/> drives which block renders. Saving preserves the
/// event's status — see <see cref="Humans.Application.Interfaces.Events.IEventService.AdminUpdateAsync"/>.
/// </summary>
public class AdminEventFormViewModel
{
    public Guid Id { get; set; }

    /// <summary>True for camp/barrio events (priority rank shown, venue hidden).</summary>
    public bool IsCampEvent { get; set; }

    /// <summary>Display-only camp name for camp events; not editable here.</summary>
    public string? CampName { get; set; }

    /// <summary>Display-only current status; preserved on save.</summary>
    public EventStatus Status { get; set; }

    [Required]
    [MaxLength(80)]
    public string Title { get; set; } = string.Empty;

    [Required]
    [MaxLength(450)]
    public string Description { get; set; } = string.Empty;

    [Required]
    [Display(Name = "Category")]
    public Guid CategoryId { get; set; }

    // Individual events only; presence validated in the controller when !IsCampEvent.
    [Display(Name = "Venue")]
    public Guid? VenueId { get; set; }

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

    // Camp events only.
    [Range(1, 100)]
    [Display(Name = "Priority Rank")]
    public int PriorityRank { get; set; } = 1;

    [MaxLength(500)]
    [Display(Name = "Edit note (optional)")]
    public string? Note { get; set; }

    // Dropdown data
    public List<CategoryOptionViewModel> Categories { get; set; } = [];
    public List<VenueOptionViewModel> Venues { get; set; } = [];
    public List<EventDayOptionViewModel> EventDays { get; set; } = [];
    public string? TimeZoneId { get; set; }
}
