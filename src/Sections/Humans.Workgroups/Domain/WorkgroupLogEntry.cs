using NodaTime;

namespace Humans.Workgroups.Domain;

/// <summary>
/// One dated line in the group's history. The log is a working record, not an
/// append-only ledger: member entries are editable and deletable (audited), system
/// entries never change. The immutable record is the audit trail.
/// </summary>
internal sealed class WorkgroupLogEntry
{
    public Guid Id { get; init; }

    public Guid WorkgroupId { get; set; }

    public WorkgroupLogKind Kind { get; set; }

    /// <summary>Editable on member entries (bootstrapping backdates); system entries use the action date.</summary>
    public LocalDate OccurredOn { get; set; }

    public string? Title { get; set; }

    /// <summary>Markdown, sanitized on render.</summary>
    public string? Body { get; set; }

    /// <summary>Bare cross-section reference; null for system entries and after erasure.</summary>
    public Guid? AuthorUserId { get; set; }

    /// <summary>The document this entry is about, when it is about one.</summary>
    public Guid? DocumentId { get; set; }

    /// <summary>Bare cross-section reference to a survey in Surveys — no FK, no navigation.</summary>
    public Guid? SurveyId { get; set; }

    public Instant CreatedAt { get; init; }

    public Instant UpdatedAt { get; set; }

    // Navigation properties (intra-section).

    public Workgroup Workgroup { get; set; } = null!;

    public WorkgroupDocument? Document { get; set; }
}
