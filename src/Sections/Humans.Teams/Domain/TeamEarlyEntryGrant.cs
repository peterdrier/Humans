using NodaTime;

namespace Humans.Teams.Domain;

internal sealed class TeamEarlyEntryGrant
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid TeamId { get; init; }
    public Team Team { get; init; } = null!;
    public Guid UserId { get; init; }
    public LocalDate EntryDate { get; set; }
    public string ProjectName { get; set; } = string.Empty;
    public Instant CreatedAt { get; init; }
    public Guid CreatedByUserId { get; init; }
    public Instant? UpdatedAt { get; set; }
}
