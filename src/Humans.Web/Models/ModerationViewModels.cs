using System.ComponentModel.DataAnnotations;
using Humans.Domain.Enums;

namespace Humans.Web.Models;

public class ModerationQueueViewModel
{
    public GuideEventStatus ActiveTab { get; set; } = GuideEventStatus.Pending;
    public int PendingCount { get; set; }
    public int ApprovedCount { get; set; }
    public int RejectedCount { get; set; }
    public int ResubmitRequestedCount { get; set; }
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
    public GuideEventStatus Status { get; set; }
    public List<ModerationHistoryItemViewModel> History { get; set; } = [];
    public List<DuplicateCandidateViewModel> DuplicateCandidates { get; set; } = [];

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

public class ModerationHistoryItemViewModel
{
    public string ActorName { get; set; } = string.Empty;
    public ModerationActionType Action { get; set; }
    public string? Reason { get; set; }
    public DateTime CreatedAt { get; set; }

    public string ActionBadgeClass => Action switch
    {
        ModerationActionType.Approved => "bg-success",
        ModerationActionType.Rejected => "bg-danger",
        ModerationActionType.ResubmitRequested => "bg-info",
        _ => "bg-secondary"
    };
}

public class DuplicateCandidateViewModel
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public DateTime StartAt { get; set; }
    public int DurationMinutes { get; set; }
    public GuideEventStatus Status { get; set; }
}

public class ModerationActionFormModel
{
    public Guid EventId { get; set; }

    [MaxLength(500)]
    public string? Reason { get; set; }
}
