using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Domain.Entities;

public class CampLead
{
    public Guid Id { get; init; }

    public Guid CampId { get; init; }
    public Camp Camp { get; set; } = null!;

    public Guid UserId { get; init; }

    public CampLeadRole Role { get; set; }

    public Instant JoinedAt { get; init; }
    public Instant? LeftAt { get; set; }

    public bool IsActive => LeftAt is null;
}
