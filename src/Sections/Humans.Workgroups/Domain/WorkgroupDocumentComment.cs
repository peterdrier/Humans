using NodaTime;

namespace Humans.Workgroups.Domain;

/// <summary>
/// A signed-in human's remark on a published document during its comment period,
/// tagged with one of the document's categories and answered by the group. The
/// resolution's "show what you heard and decided against" is this record, so the
/// body is kept indefinitely even after the author's attribution is erased.
/// </summary>
internal sealed class WorkgroupDocumentComment
{
    public Guid Id { get; init; }

    public Guid DocumentId { get; set; }

    /// <summary>One of the document's categories as they stood when the comment was posted.</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>Bare cross-section reference; null after erasure — the body stays.</summary>
    public Guid? AuthorUserId { get; set; }

    /// <summary>Markdown, sanitized on render.</summary>
    public string Body { get; set; } = string.Empty;

    public Instant CreatedAt { get; init; }

    public WorkgroupCommentDisposition Disposition { get; set; }

    /// <summary>The group's answer; visible to everyone once set.</summary>
    public string? Response { get; set; }

    /// <summary>Bare cross-section reference; nulled on erasure.</summary>
    public Guid? RespondedByUserId { get; set; }

    public Instant? RespondedAt { get; set; }

    /// <summary>Moderation: hidden comments read "hidden by the group", full text to admins.</summary>
    public Instant? HiddenAt { get; set; }

    /// <summary>Bare cross-section reference; nulled on erasure.</summary>
    public Guid? HiddenByUserId { get; set; }

    public string? HiddenReason { get; set; }

    // Navigation properties (intra-section).

    public WorkgroupDocument Document { get; set; } = null!;
}
