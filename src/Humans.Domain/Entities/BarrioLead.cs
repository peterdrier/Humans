using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Domain.Entities;

public class BarrioLead
{
    public Guid Id { get; init; }

    public Guid BarrioId { get; init; }
    public Barrio Barrio { get; set; } = null!;

    public Guid UserId { get; init; }
    public User User { get; set; } = null!;

    public BarrioLeadRole Role { get; set; }

    public Instant JoinedAt { get; init; }
    public Instant? LeftAt { get; set; }

    public bool IsActive => LeftAt is null;
}
