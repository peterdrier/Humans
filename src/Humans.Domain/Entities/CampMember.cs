using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Domain.Entities;

/// <summary>
/// A human's membership in a camp for a given season. This is a post-hoc record —
/// humans join a camp through the camp's own process (website, spreadsheet, WhatsApp) first,
/// then create a CampMember so Humans knows about the relationship for roles, early entry,
/// and notifications.
///
/// Per-season, not per-camp. Privacy: never rendered on anonymous or public views.
/// </summary>
public class CampMember
{
    public Guid Id { get; init; }

    public Guid CampSeasonId { get; init; }
    public CampSeason CampSeason { get; set; } = null!;

    public Guid UserId { get; init; }
    public User User { get; set; } = null!;

    public CampMemberStatus Status { get; set; } = CampMemberStatus.Pending;

    public Instant RequestedAt { get; init; }

    public Instant? ConfirmedAt { get; set; }
    public Guid? ConfirmedByUserId { get; set; }
    public User? ConfirmedByUser { get; set; }

    public Instant? RemovedAt { get; set; }
    public Guid? RemovedByUserId { get; set; }
    public User? RemovedByUser { get; set; }
}
