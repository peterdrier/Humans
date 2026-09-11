using NodaTime;

namespace Humans.Workgroups.Domain;

/// <summary>
/// A dated session the group owns. Public meetings reach everyone's community
/// calendar through Calendar's contributor fan-out; the rest are members-only.
/// Minutes ("or summarised transcripts", per the guidance) are filled in afterwards.
/// </summary>
internal sealed class WorkgroupMeeting
{
    public Guid Id { get; init; }

    public Guid WorkgroupId { get; set; }

    public string Title { get; set; } = string.Empty;

    public Instant StartUtc { get; set; }

    public Instant EndUtc { get; set; }

    public string? Location { get; set; }

    public string? LocationUrl { get; set; }

    /// <summary>Public meetings appear on the community calendar for everyone.</summary>
    public bool IsPublic { get; set; }

    /// <summary>Markdown, sanitized on render.</summary>
    public string? Minutes { get; set; }

    /// <summary>Bare cross-section reference; nulled on erasure.</summary>
    public Guid? CreatedByUserId { get; set; }

    public Instant CreatedAt { get; init; }

    public Instant UpdatedAt { get; set; }

    /// <summary>Soft delete: a cancelled meeting leaves the log's history intact.</summary>
    public Instant? DeletedAt { get; set; }

    // Navigation properties (intra-section).

    public Workgroup Workgroup { get; set; } = null!;
}
