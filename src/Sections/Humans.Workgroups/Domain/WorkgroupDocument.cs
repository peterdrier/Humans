using NodaTime;

namespace Humans.Workgroups.Domain;

/// <summary>One body, last write wins: groups draft in their Drive folder and paste the publishable Markdown here.</summary>
internal sealed class WorkgroupDocument
{
    public Guid Id { get; init; }

    public Guid WorkgroupId { get; set; }

    public string Title { get; set; } = string.Empty;

    public WorkgroupDocumentKind Kind { get; set; }

    public string Body { get; set; } = string.Empty;

    public WorkgroupDocumentStatus Status { get; set; }

    /// <summary>Defined by the group before the comment period opens ("Scope", "Wording", "Timeline").</summary>
    public List<string> CommentCategories { get; set; } = [];

    public Instant? CommentsOpenAt { get; set; }

    public Instant? CommentsCloseAt { get; set; }

    public Instant? DeliveredAt { get; set; }

    /// <summary>Only ever set on a Delivered document.</summary>
    public WorkgroupDisposition? Disposition { get; set; }

    public string? DispositionNote { get; set; }

    public Instant? DispositionAt { get; set; }

    public Guid? DispositionByUserId { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public Guid? UpdatedByUserId { get; set; }

    public Instant CreatedAt { get; init; }

    public Instant UpdatedAt { get; set; }

    public Workgroup Workgroup { get; set; } = null!;

    public ICollection<WorkgroupDocumentComment> Comments { get; set; } = new List<WorkgroupDocumentComment>();
}
