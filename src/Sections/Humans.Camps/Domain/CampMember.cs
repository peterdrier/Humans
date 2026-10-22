using NodaTime;

namespace Humans.Camps.Domain;

/// <summary>
/// A post-hoc record: humans join through the camp's own process (website, spreadsheet, WhatsApp) first, then are
/// recorded here. Per-season, not per-camp. Privacy: never rendered on anonymous or public views.
/// </summary>
internal sealed class CampMember
{
    public Guid Id { get; init; }

    public Guid CampSeasonId { get; init; }
    public CampSeason CampSeason { get; set; } = null!;

    public Guid UserId { get; init; }

    public CampMemberStatus Status { get; set; } = CampMemberStatus.Pending;

    public Instant RequestedAt { get; init; }

    public Instant? ConfirmedAt { get; set; }
    public Guid? ConfirmedByUserId { get; set; }

    public Instant? RemovedAt { get; set; }
    public Guid? RemovedByUserId { get; set; }

    /// <summary>Cleared on member removal. Never rendered on anonymous or public views.</summary>
    public bool HasEarlyEntry { get; set; }
}
