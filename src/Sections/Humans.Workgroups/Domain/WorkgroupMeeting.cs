using NodaTime;

namespace Humans.Workgroups.Domain;

internal sealed class WorkgroupMeeting
{
    public Guid Id { get; init; }

    public Guid WorkgroupId { get; set; }

    public string Title { get; set; } = string.Empty;

    public Instant StartUtc { get; set; }

    public Instant EndUtc { get; set; }

    /// <summary>RFC 5545 RRULE; null for a one-off meeting. <see cref="StartUtc"/>..<see cref="EndUtc"/> is the first occurrence.</summary>
    public string? RecurrenceRule { get; set; }

    /// <summary>IANA zone the rule repeats in, so a weekly 19:00 stays 19:00 across DST. Set exactly when the rule is.</summary>
    public string? RecurrenceTimezone { get; set; }

    /// <summary>What the meeting is for, written before it. Notes after it are <see cref="WorkgroupLogKind.MeetingNotes"/> log entries.</summary>
    public string? Description { get; set; }

    public string? Location { get; set; }

    public string? LocationUrl { get; set; }

    public bool IsPublic { get; set; }

    /// <summary>Legacy single minutes box, shown read-only; new notes are log entries.</summary>
    public string? Minutes { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public Instant CreatedAt { get; init; }

    public Instant UpdatedAt { get; set; }

    /// <summary>Soft delete, so a cancelled meeting leaves the log's history intact.</summary>
    public Instant? DeletedAt { get; set; }

    public Workgroup Workgroup { get; set; } = null!;
}
