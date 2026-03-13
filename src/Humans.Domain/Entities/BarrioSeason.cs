using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Domain.Entities;

public class BarrioSeason
{
    public Guid Id { get; init; }

    public Guid BarrioId { get; init; }
    public Barrio Barrio { get; set; } = null!;

    public int Year { get; init; }
    public string Name { get; set; } = string.Empty;
    public LocalDate? NameLockDate { get; set; }
    public Instant? NameLockedAt { get; set; }

    public BarrioSeasonStatus Status { get; set; } = BarrioSeasonStatus.Pending;

    public string BlurbLong { get; set; } = string.Empty;
    public string BlurbShort { get; set; } = string.Empty;
    public string Languages { get; set; } = string.Empty;

    public YesNoMaybe AcceptingMembers { get; set; }
    public YesNoMaybe KidsWelcome { get; set; }
    public KidsVisitingPolicy KidsVisiting { get; set; }
    public string? KidsAreaDescription { get; set; }

    public PerformanceSpaceStatus HasPerformanceSpace { get; set; }
    public string? PerformanceTypes { get; set; }

    public List<BarrioVibe> Vibes { get; set; } = new();

    public AdultPlayspacePolicy AdultPlayspace { get; set; }

    // Placement
    public int MemberCount { get; set; }
    public SpaceSize? SpaceRequirement { get; set; }
    public SoundZone? SoundZone { get; set; }
    public int ContainerCount { get; set; }
    public string? ContainerNotes { get; set; }
    public ElectricalGrid? ElectricalGrid { get; set; }

    // Review
    public Guid? ReviewedByUserId { get; set; }
    public User? ReviewedByUser { get; set; }
    public string? ReviewNotes { get; set; }
    public Instant? ResolvedAt { get; set; }

    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }
}
