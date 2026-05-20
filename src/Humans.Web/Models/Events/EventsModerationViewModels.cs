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
