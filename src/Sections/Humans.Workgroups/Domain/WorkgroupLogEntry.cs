using NodaTime;

namespace Humans.Workgroups.Domain;

/// <summary>A working record, not an append-only ledger: the audit trail is the immutable one.</summary>
internal sealed class WorkgroupLogEntry
{
    public Guid Id { get; init; }

    public Guid WorkgroupId { get; set; }

    public WorkgroupLogKind Kind { get; set; }

    /// <summary>Editable on member entries; system entries use the action date.</summary>
    public LocalDate OccurredOn { get; set; }

    public string? Title { get; set; }

    public string? Body { get; set; }

    /// <summary>Null for system entries.</summary>
    public Guid? AuthorUserId { get; set; }

    public Guid? DocumentId { get; set; }

    public Guid? SurveyId { get; set; }

    public Instant CreatedAt { get; init; }

    public Instant UpdatedAt { get; set; }

    public Workgroup Workgroup { get; set; } = null!;

    public WorkgroupDocument? Document { get; set; }
}
