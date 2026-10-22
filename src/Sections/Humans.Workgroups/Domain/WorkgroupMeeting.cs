using NodaTime;

namespace Humans.Workgroups.Domain;

internal sealed class WorkgroupMeeting
{
    public Guid Id { get; init; }

    public Guid WorkgroupId { get; set; }

    public string Title { get; set; } = string.Empty;

    public Instant StartUtc { get; set; }

    public Instant EndUtc { get; set; }

    public string? Location { get; set; }

    public string? LocationUrl { get; set; }

    public bool IsPublic { get; set; }

    public string? Minutes { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public Instant CreatedAt { get; init; }

    public Instant UpdatedAt { get; set; }

    /// <summary>Soft delete, so a cancelled meeting leaves the log's history intact.</summary>
    public Instant? DeletedAt { get; set; }

    public Workgroup Workgroup { get; set; } = null!;
}
