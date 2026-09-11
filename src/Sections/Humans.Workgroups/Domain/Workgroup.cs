using NodaTime;

namespace Humans.Workgroups.Domain;

/// <summary>
/// One entry in the register of association-level working groups the Board resolution
/// obliges the Secretary to keep. Intentionally time-bound: a recurring subject is a
/// new instance per year ("Finance 2027"), never a long-lived group.
/// </summary>
internal sealed class Workgroup
{
    public Guid Id { get; init; }

    /// <summary>Register name; year instances carry the year in the name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Unique; generated from <see cref="Name"/>, admin-editable.</summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>Markdown, sanitized on render.</summary>
    public string Purpose { get; set; } = string.Empty;

    /// <summary>One line: the artefact the group will deliver.</summary>
    public string Deliverable { get; set; } = string.Empty;

    public WorkgroupDeliverableKind DeliverableKind { get; set; }

    public WorkgroupAudience Audience { get; set; }

    /// <summary>Expected delivery, or the event date for <see cref="WorkgroupDeliverableKind.Event"/>.</summary>
    public LocalDate? TargetDate { get; set; }

    public WorkgroupStatus Status { get; set; }

    /// <summary>Null unless <see cref="Status"/> is <see cref="WorkgroupStatus.Dormant"/>.</summary>
    public WorkgroupDormantReason? DormantReason { get; set; }

    /// <summary>Google file id of the group's Drive subfolder; null until Active.</summary>
    public string? DriveFolderId { get; set; }

    public string? DiscordChannelUrl { get; set; }

    /// <summary>Refusal or withdrawal reasons; required for Refused, Withdrawn and a Quiet close.</summary>
    public string? Reasons { get; set; }

    /// <summary>Who applied. Bare cross-section reference — no FK; nulled on erasure.</summary>
    public Guid? AppliedByUserId { get; set; }

    public Instant AppliedAt { get; set; }

    public Instant? RegisteredAt { get; set; }

    public Instant? EndedAt { get; set; }

    /// <summary>
    /// Set by the daily job on sixty days' silence and cleared by the next Update or
    /// Meeting. Distinct from the Dormant <see cref="Status"/>: this is the inquiry flag,
    /// not the end of the group.
    /// </summary>
    public Instant? DormantSince { get; set; }

    public Instant CreatedAt { get; init; }

    public Instant UpdatedAt { get; set; }

    // Navigation properties (intra-section).

    public ICollection<WorkgroupMember> Members { get; set; } = new List<WorkgroupMember>();

    public ICollection<WorkgroupMeeting> Meetings { get; set; } = new List<WorkgroupMeeting>();

    public ICollection<WorkgroupLogEntry> LogEntries { get; set; } = new List<WorkgroupLogEntry>();

    public ICollection<WorkgroupDocument> Documents { get; set; } = new List<WorkgroupDocument>();
}
