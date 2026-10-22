using NodaTime;

namespace Humans.Workgroups.Domain;

/// <summary>
/// The resolution's "show what you heard and decided against" is this record, so the body stays after the
/// author's attribution is erased.
/// </summary>
internal sealed class WorkgroupDocumentComment
{
    public Guid Id { get; init; }

    public Guid DocumentId { get; set; }

    /// <summary>One of the document's categories as they stood when posted.</summary>
    public string Category { get; set; } = string.Empty;

    public Guid? AuthorUserId { get; set; }

    public string Body { get; set; } = string.Empty;

    public Instant CreatedAt { get; init; }

    public WorkgroupCommentDisposition Disposition { get; set; }

    /// <summary>Visible to everyone once set.</summary>
    public string? Response { get; set; }

    public Guid? RespondedByUserId { get; set; }

    public Instant? RespondedAt { get; set; }

    /// <summary>Hidden comments read "hidden by the group"; admins see the full text.</summary>
    public Instant? HiddenAt { get; set; }

    public Guid? HiddenByUserId { get; set; }

    public string? HiddenReason { get; set; }

    public WorkgroupDocument Document { get; set; } = null!;
}
