using NodaTime;

namespace Humans.Domain.Entities;

/// <summary>
/// One early-entry grant owned by the Teams section: a human (<see cref="UserId"/>)
/// may enter on <see cref="EntryDate"/> for a named project. Surfaced to the
/// cross-section EE roster as "{TeamName}: {ProjectName}". No EF navigation to User —
/// the bare FK is resolved through IUserServiceRead at the service layer.
/// </summary>
public sealed class TeamEarlyEntryGrant
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid TeamId { get; init; }
    public Team Team { get; init; } = null!;   // FK nav to owning team only (same-section)
    public Guid UserId { get; init; }
    public LocalDate EntryDate { get; set; }
    public string ProjectName { get; set; } = string.Empty;
    public Instant CreatedAt { get; init; }
    public Guid CreatedByUserId { get; init; }
    public Instant? UpdatedAt { get; set; }
}
