using NodaTime;

namespace Humans.Workgroups.Domain;

/// <summary>Time-bound: a recurring subject is a new instance per year ("Finance 2027"), never a long-lived group.</summary>
internal sealed class Workgroup
{
    public Guid Id { get; init; }

    public string Name { get; set; } = string.Empty;

    /// <summary>The index is plain: uniqueness is the service's reservation loop, not a constraint.</summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>Markdown, sanitized on render.</summary>
    public string Purpose { get; set; } = string.Empty;

    public string Deliverable { get; set; } = string.Empty;

    public WorkgroupDeliverableKind DeliverableKind { get; set; }

    public WorkgroupAudience Audience { get; set; }

    /// <summary>Expected delivery, or the event date for <see cref="WorkgroupDeliverableKind.Event"/>.</summary>
    public LocalDate? TargetDate { get; set; }

    public WorkgroupStatus Status { get; set; }

    public WorkgroupDormantReason? DormantReason { get; set; }

    /// <summary>Null until Active.</summary>
    public string? DriveFolderId { get; set; }

    public string? DiscordChannelUrl { get; set; }

    /// <summary>Nullable so the column adds no physical default; null reads as not sensitive.</summary>
    public bool? IsSensitive { get; set; }

    /// <summary>EUR; null means no budget. Finance and Expenses never read it.</summary>
    public decimal? BudgetAmount { get; set; }

    /// <summary>Opaque external reference, no FK. Kept when the budget is cleared: Holded accounts are never deleted.</summary>
    public int? HoldedAccountNumber { get; set; }

    public string? HoldedAccountId { get; set; }

    /// <summary>Required for Refused, Withdrawn and a Quiet close.</summary>
    public string? Reasons { get; set; }

    /// <summary>Nulled on erasure.</summary>
    public Guid? AppliedByUserId { get; set; }

    public Instant AppliedAt { get; set; }

    public Instant? RegisteredAt { get; set; }

    public Instant? EndedAt { get; set; }

    public Instant CreatedAt { get; init; }

    public Instant UpdatedAt { get; set; }

    public ICollection<WorkgroupMember> Members { get; set; } = new List<WorkgroupMember>();

    public ICollection<WorkgroupMeeting> Meetings { get; set; } = new List<WorkgroupMeeting>();

    public ICollection<WorkgroupLogEntry> LogEntries { get; set; } = new List<WorkgroupLogEntry>();

    public ICollection<WorkgroupDocument> Documents { get; set; } = new List<WorkgroupDocument>();
}
