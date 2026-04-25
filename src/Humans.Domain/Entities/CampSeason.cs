using Humans.Domain.Attributes;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Domain.Entities;

public class CampSeason
{
    public Guid Id { get; init; }

    public Guid CampId { get; init; }
    public Camp Camp { get; set; } = null!;

    public int Year { get; init; }
    public string Name { get; set; } = string.Empty;
    public LocalDate? NameLockDate { get; set; }
    public Instant? NameLockedAt { get; set; }

    public CampSeasonStatus Status { get; set; } = CampSeasonStatus.Pending;

    [MarkdownContent]
    public string BlurbLong { get; set; } = string.Empty;
    public string BlurbShort { get; set; } = string.Empty;
    public string Languages { get; set; } = string.Empty;

    public YesNoMaybe AcceptingMembers { get; set; }
    public YesNoMaybe KidsWelcome { get; set; }
    public KidsVisitingPolicy KidsVisiting { get; set; }
    [MarkdownContent]
    public string? KidsAreaDescription { get; set; }

    public PerformanceSpaceStatus HasPerformanceSpace { get; set; }
    public string? PerformanceTypes { get; set; }

    public List<CampVibe> Vibes { get; set; } = new();

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

    public ICollection<CampRoleAssignment> RoleAssignments { get; } = new List<CampRoleAssignment>();
}
